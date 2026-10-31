using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ParentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: false),
                    ValidationState = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisRevisions", x => x.Id);
                    table.CheckConstraint("CK_AnalysisRevisions_Confirmation", "(\"ValidationState\" = 'Unvalidated' AND \"ConfirmedBy\" IS NULL AND \"ConfirmedAt\" IS NULL) OR (\"ValidationState\" = 'PmConfirmed' AND \"ConfirmedBy\" IS NOT NULL AND \"ConfirmedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_AnalysisRevisions_Origin_Fields", "(\"Origin\" = 'Ai' AND \"Version\" = 1 AND \"ParentRevisionId\" IS NULL) OR (\"Origin\" = 'PmEdit' AND \"Version\" >= 2 AND \"ParentRevisionId\" IS NOT NULL)");
                    table.CheckConstraint("CK_AnalysisRevisions_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_AnalysisRevisions_AnalysisRevisions_ParentRevisionId",
                        column: x => x.ParentRevisionId,
                        principalTable: "AnalysisRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisRevisions_AspNetUsers_ConfirmedBy",
                        column: x => x.ConfirmedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisRevisions_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceCutoffSequence = table.Column<long>(type: "bigint", nullable: true),
                    AnalysisId = table.Column<Guid>(type: "uuid", nullable: true),
                    InputRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WorkflowDefinitionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MafVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AgentConfig = table.Column<string>(type: "jsonb", nullable: true),
                    PromptVersions = table.Column<string>(type: "jsonb", nullable: true),
                    WorkflowSessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    LastObservedStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DraftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingRuns", x => x.Id);
                    table.CheckConstraint("CK_ProcessingRuns_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                    table.CheckConstraint("CK_ProcessingRuns_Completed_Result", "\"Status\" <> 'Completed' OR (\"Kind\" = 'Analysis' AND \"ResultRevisionId\" IS NOT NULL) OR (\"Kind\" = 'Document' AND \"DraftId\" IS NOT NULL)");
                    table.CheckConstraint("CK_ProcessingRuns_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.CheckConstraint("CK_ProcessingRuns_Kind_Fields", "(\"Kind\" = 'Analysis' AND \"ConversationId\" IS NOT NULL AND \"SourceCutoffSequence\" IS NOT NULL AND \"SourceCutoffSequence\" >= 0 AND \"AnalysisId\" IS NULL AND \"InputRevisionId\" IS NULL AND \"DraftId\" IS NULL) OR (\"Kind\" = 'Document' AND \"AnalysisId\" IS NOT NULL AND \"InputRevisionId\" IS NOT NULL AND \"ResultRevisionId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_AnalysisRevisions_InputRevisionId",
                        column: x => x.InputRevisionId,
                        principalTable: "AnalysisRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_AnalysisRevisions_ResultRevisionId",
                        column: x => x.ResultRevisionId,
                        principalTable: "AnalysisRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_ProcessingRuns_AnalysisId",
                        column: x => x.AnalysisId,
                        principalTable: "ProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProcessingRuns_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CurrentAnalysisId",
                table: "Projects",
                column: "CurrentAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRevisions_AnalysisId_Version",
                table: "AnalysisRevisions",
                columns: new[] { "AnalysisId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRevisions_ConfirmedBy",
                table: "AnalysisRevisions",
                column: "ConfirmedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRevisions_CreatedBy",
                table: "AnalysisRevisions",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRevisions_OneAiPerAnalysis",
                table: "AnalysisRevisions",
                column: "AnalysisId",
                unique: true,
                filter: "\"Origin\" = 'Ai'");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRevisions_ParentRevisionId",
                table: "AnalysisRevisions",
                column: "ParentRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_AnalysisId",
                table: "ProcessingRuns",
                column: "AnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_ConversationId",
                table: "ProcessingRuns",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_InputRevisionId",
                table: "ProcessingRuns",
                column: "InputRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_OneActivePerProject",
                table: "ProcessingRuns",
                column: "ProjectId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_Open_Fifo",
                table: "ProcessingRuns",
                columns: new[] { "RequestedAt", "Id" },
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_ProjectId_Kind_RequestKey",
                table: "ProcessingRuns",
                columns: new[] { "ProjectId", "Kind", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_RequestedBy",
                table: "ProcessingRuns",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_ResultRevisionId",
                table: "ProcessingRuns",
                column: "ResultRevisionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_ProcessingRuns_CurrentAnalysisId",
                table: "Projects",
                column: "CurrentAnalysisId",
                principalTable: "ProcessingRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisRevisions_ProcessingRuns_AnalysisId",
                table: "AnalysisRevisions",
                column: "AnalysisId",
                principalTable: "ProcessingRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_ProcessingRuns_CurrentAnalysisId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisRevisions_ProcessingRuns_AnalysisId",
                table: "AnalysisRevisions");

            migrationBuilder.DropTable(
                name: "ProcessingRuns");

            migrationBuilder.DropTable(
                name: "AnalysisRevisions");

            migrationBuilder.DropIndex(
                name: "IX_Projects_CurrentAnalysisId",
                table: "Projects");
        }
    }
}
