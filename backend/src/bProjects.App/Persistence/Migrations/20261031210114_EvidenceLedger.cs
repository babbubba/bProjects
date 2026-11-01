using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls");

            migrationBuilder.AddColumn<Guid>(
                name: "EvidenceExtractionJobId",
                table: "InferenceCalls",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AnalysisEvidenceSelections",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ViewHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    View = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisEvidenceSelections", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceSelections_ProcessingRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceSelections_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    JobCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceBatches", x => x.Id);
                    table.CheckConstraint("CK_EvidenceBatches_Counts", "\"JobCount\" >= 0 AND \"SkippedCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_EvidenceBatches_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceBatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceCoverageReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckerVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeterministicErrors = table.Column<int>(type: "integer", nullable: false),
                    SemanticRisks = table.Column<int>(type: "integer", nullable: false),
                    PmAssessments = table.Column<int>(type: "integer", nullable: false),
                    Report = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceCoverageReports", x => x.Id);
                    table.CheckConstraint("CK_EvidenceCoverageReports_Counts", "\"DeterministicErrors\" >= 0 AND \"SemanticRisks\" >= 0 AND \"PmAssessments\" >= 0");
                    table.ForeignKey(
                        name: "FK_EvidenceCoverageReports_AnalysisRevisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "AnalysisRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceCoverageReports_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceCoverageReports_Drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "Drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceCoverageReports_ProcessingRuns_AnalysisId",
                        column: x => x.AnalysisId,
                        principalTable: "ProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceCoverageReports_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisEvidenceLinks",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisEvidenceLinks", x => new { x.RunId, x.ItemId, x.EvidenceId });
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceLinks_AnalysisEvidenceSelections_RunId",
                        column: x => x.RunId,
                        principalTable: "AnalysisEvidenceSelections",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisEvidenceSelectionItems",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisEvidenceSelectionItems", x => new { x.RunId, x.MessageId });
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceSelectionItems_AnalysisEvidenceSelections_R~",
                        column: x => x.RunId,
                        principalTable: "AnalysisEvidenceSelections",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceSelectionItems_ConversationMessages_Message~",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceExtractionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetryOfJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ExtractorConfig = table.Column<string>(type: "jsonb", nullable: false),
                    ResultVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceExtractionJobs", x => x.Id);
                    table.CheckConstraint("CK_EvidenceExtractionJobs_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                    table.CheckConstraint("CK_EvidenceExtractionJobs_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultVersionId\" IS NOT NULL");
                    table.CheckConstraint("CK_EvidenceExtractionJobs_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.CheckConstraint("CK_EvidenceExtractionJobs_Sequence", "\"MessageSequence\" >= 1");
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_EvidenceBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "EvidenceBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_EvidenceExtractionJobs_RetryOfJobId",
                        column: x => x.RetryOfJobId,
                        principalTable: "EvidenceExtractionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionJobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceExtractionVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageSequence = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceCount = table.Column<int>(type: "integer", nullable: false),
                    NoEvidenceReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceExtractionVersions", x => x.Id);
                    table.CheckConstraint("CK_EvidenceExtractionVersions_NoEvidence", "\"EvidenceCount\" >= 0 AND ((\"EvidenceCount\" = 0) = (\"NoEvidenceReason\" IS NOT NULL)) AND (\"NoEvidenceReason\" IS NULL OR \"NoEvidenceReason\" ~ '\\S')");
                    table.CheckConstraint("CK_EvidenceExtractionVersions_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionVersions_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionVersions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionVersions_EvidenceExtractionJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "EvidenceExtractionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceExtractionVersions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceMessageStates",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceMessageStates", x => x.MessageId);
                    table.CheckConstraint("CK_EvidenceMessageStates_Revision", "\"Revision\" >= 1");
                    table.ForeignKey(
                        name: "FK_EvidenceMessageStates_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceMessageStates_EvidenceExtractionVersions_CurrentVer~",
                        column: x => x.CurrentVersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceMessageStates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidencePromotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RelationImpact = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidencePromotions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EvidencePromotions_AspNetUsers_PromotedBy",
                        column: x => x.PromotedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidencePromotions_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidencePromotions_EvidenceExtractionVersions_FromVersionId",
                        column: x => x.FromVersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidencePromotions_EvidenceExtractionVersions_ToVersionId",
                        column: x => x.ToVersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidencePromotions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Evidences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Polarity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Certainty = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Evidences", x => x.Id);
                    table.CheckConstraint("CK_Evidences_Ordinal", "\"Ordinal\" >= 1");
                    table.CheckConstraint("CK_Evidences_Statement_NotBlank", "\"Statement\" ~ '\\S'");
                    table.ForeignKey(
                        name: "FK_Evidences_ConversationMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidences_EvidenceExtractionVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Evidences_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceRelations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceRelations", x => x.Id);
                    table.CheckConstraint("CK_EvidenceRelations_NotSelf", "\"FromEvidenceId\" <> \"ToEvidenceId\"");
                    table.ForeignKey(
                        name: "FK_EvidenceRelations_EvidenceExtractionVersions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceRelations_Evidences_FromEvidenceId",
                        column: x => x.FromEvidenceId,
                        principalTable: "Evidences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceRelations_Evidences_ToEvidenceId",
                        column: x => x.ToEvidenceId,
                        principalTable: "Evidences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceRelations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_EvidenceExtractionJobId_Attempt",
                table: "InferenceCalls",
                columns: new[] { "EvidenceExtractionJobId", "Attempt" });

            migrationBuilder.CreateIndex(
                name: "UX_InferenceCalls_OneFinalPerExtraction",
                table: "InferenceCalls",
                column: "EvidenceExtractionJobId",
                unique: true,
                filter: "\"IsFinal\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls",
                sql: "(\"ChatTurnId\" IS NOT NULL)::int + (\"ProcessingRunId\" IS NOT NULL)::int + (\"EvidenceExtractionJobId\" IS NOT NULL)::int = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceLinks_EvidenceId",
                table: "AnalysisEvidenceLinks",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceLinks_ProjectId",
                table: "AnalysisEvidenceLinks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelectionItems_MessageId",
                table: "AnalysisEvidenceSelectionItems",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelectionItems_VersionId",
                table: "AnalysisEvidenceSelectionItems",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelections_ProjectId",
                table: "AnalysisEvidenceSelections",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceBatches_ProjectId_Kind_RequestKey",
                table: "EvidenceBatches",
                columns: new[] { "ProjectId", "Kind", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceBatches_RequestedBy",
                table: "EvidenceBatches",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCoverageReports_AnalysisId_RevisionId_DraftId_Check~",
                table: "EvidenceCoverageReports",
                columns: new[] { "AnalysisId", "RevisionId", "DraftId", "CheckerVersion" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCoverageReports_CreatedBy",
                table: "EvidenceCoverageReports",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCoverageReports_DraftId",
                table: "EvidenceCoverageReports",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCoverageReports_ProjectId_CreatedAt",
                table: "EvidenceCoverageReports",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceCoverageReports_RevisionId",
                table: "EvidenceCoverageReports",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_BatchId",
                table: "EvidenceExtractionJobs",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_ConversationId",
                table: "EvidenceExtractionJobs",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_Open_Fifo",
                table: "EvidenceExtractionJobs",
                columns: new[] { "RequestedAt", "Id" },
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_ProjectId_MessageSequence",
                table: "EvidenceExtractionJobs",
                columns: new[] { "ProjectId", "MessageSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_ProjectId_RequestKey",
                table: "EvidenceExtractionJobs",
                columns: new[] { "ProjectId", "RequestKey" },
                unique: true,
                filter: "\"RequestKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_RequestedBy",
                table: "EvidenceExtractionJobs",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_ResultVersionId",
                table: "EvidenceExtractionJobs",
                column: "ResultVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_RetryOfJobId",
                table: "EvidenceExtractionJobs",
                column: "RetryOfJobId");

            migrationBuilder.CreateIndex(
                name: "UX_EvidenceExtractionJobs_OneOpenPerMessage",
                table: "EvidenceExtractionJobs",
                column: "MessageId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_ConversationId",
                table: "EvidenceExtractionVersions",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_JobId",
                table: "EvidenceExtractionVersions",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_MessageId_Version",
                table: "EvidenceExtractionVersions",
                columns: new[] { "MessageId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_ProjectId_MessageSequence",
                table: "EvidenceExtractionVersions",
                columns: new[] { "ProjectId", "MessageSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceMessageStates_CurrentVersionId",
                table: "EvidenceMessageStates",
                column: "CurrentVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceMessageStates_ProjectId",
                table: "EvidenceMessageStates",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidencePromotions_FromVersionId",
                table: "EvidencePromotions",
                column: "FromVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidencePromotions_MessageId_PromotedAt",
                table: "EvidencePromotions",
                columns: new[] { "MessageId", "PromotedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidencePromotions_ProjectId",
                table: "EvidencePromotions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidencePromotions_PromotedBy",
                table: "EvidencePromotions",
                column: "PromotedBy");

            migrationBuilder.CreateIndex(
                name: "IX_EvidencePromotions_ToVersionId",
                table: "EvidencePromotions",
                column: "ToVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRelations_FromEvidenceId",
                table: "EvidenceRelations",
                column: "FromEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRelations_ProjectId",
                table: "EvidenceRelations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRelations_ToEvidenceId",
                table: "EvidenceRelations",
                column: "ToEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceRelations_VersionId_FromEvidenceId_ToEvidenceId_Type",
                table: "EvidenceRelations",
                columns: new[] { "VersionId", "FromEvidenceId", "ToEvidenceId", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_MessageId",
                table: "Evidences",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_ProjectId_MessageId",
                table: "Evidences",
                columns: new[] { "ProjectId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_VersionId_Key",
                table: "Evidences",
                columns: new[] { "VersionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_VersionId_Ordinal",
                table: "Evidences",
                columns: new[] { "VersionId", "Ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InferenceCalls_EvidenceExtractionJobs_EvidenceExtractionJob~",
                table: "InferenceCalls",
                column: "EvidenceExtractionJobId",
                principalTable: "EvidenceExtractionJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisEvidenceLinks_Evidences_EvidenceId",
                table: "AnalysisEvidenceLinks",
                column: "EvidenceId",
                principalTable: "Evidences",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisEvidenceSelectionItems_EvidenceExtractionVersions_V~",
                table: "AnalysisEvidenceSelectionItems",
                column: "VersionId",
                principalTable: "EvidenceExtractionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EvidenceExtractionJobs_EvidenceExtractionVersions_ResultVer~",
                table: "EvidenceExtractionJobs",
                column: "ResultVersionId",
                principalTable: "EvidenceExtractionVersions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InferenceCalls_EvidenceExtractionJobs_EvidenceExtractionJob~",
                table: "InferenceCalls");

            migrationBuilder.DropForeignKey(
                name: "FK_EvidenceExtractionJobs_EvidenceExtractionVersions_ResultVer~",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropTable(
                name: "AnalysisEvidenceLinks");

            migrationBuilder.DropTable(
                name: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropTable(
                name: "EvidenceCoverageReports");

            migrationBuilder.DropTable(
                name: "EvidenceMessageStates");

            migrationBuilder.DropTable(
                name: "EvidencePromotions");

            migrationBuilder.DropTable(
                name: "EvidenceRelations");

            migrationBuilder.DropTable(
                name: "AnalysisEvidenceSelections");

            migrationBuilder.DropTable(
                name: "Evidences");

            migrationBuilder.DropTable(
                name: "EvidenceExtractionVersions");

            migrationBuilder.DropTable(
                name: "EvidenceExtractionJobs");

            migrationBuilder.DropTable(
                name: "EvidenceBatches");

            migrationBuilder.DropIndex(
                name: "IX_InferenceCalls_EvidenceExtractionJobId_Attempt",
                table: "InferenceCalls");

            migrationBuilder.DropIndex(
                name: "UX_InferenceCalls_OneFinalPerExtraction",
                table: "InferenceCalls");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls");

            migrationBuilder.DropColumn(
                name: "EvidenceExtractionJobId",
                table: "InferenceCalls");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls",
                sql: "(\"ChatTurnId\" IS NULL) <> (\"ProcessingRunId\" IS NULL)");
        }
    }
}
