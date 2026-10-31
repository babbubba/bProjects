using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Drafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProcessingRuns_Id_ProjectId",
                table: "ProcessingRuns",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.CreateTable(
                name: "Drafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessingRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    AnalysisRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Markdown = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovalStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drafts", x => x.Id);
                    table.CheckConstraint("CK_Drafts_Markdown_NotBlank", "\"Markdown\" ~ '\\S'");
                    table.CheckConstraint("CK_Drafts_Status", "\"Status\" = 'Proposed' AND \"ApprovalStatus\" = 'NotApproved'");
                    table.ForeignKey(
                        name: "FK_Drafts_AnalysisRevisions_AnalysisRevisionId",
                        column: x => x.AnalysisRevisionId,
                        principalTable: "AnalysisRevisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Drafts_ProcessingRuns_ProcessingRunId_ProjectId",
                        columns: x => new { x.ProcessingRunId, x.ProjectId },
                        principalTable: "ProcessingRuns",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Drafts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_LatestDraftId",
                table: "Projects",
                column: "LatestDraftId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_DraftId",
                table: "ProcessingRuns",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "UX_ProcessingRuns_Id_InputRevisionId",
                table: "ProcessingRuns",
                columns: new[] { "Id", "InputRevisionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_AnalysisRevisionId",
                table: "Drafts",
                column: "AnalysisRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_ProcessingRunId",
                table: "Drafts",
                column: "ProcessingRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_ProcessingRunId_ProjectId",
                table: "Drafts",
                columns: new[] { "ProcessingRunId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Drafts_ProjectId_CreatedAt",
                table: "Drafts",
                columns: new[] { "ProjectId", "CreatedAt" });

            // La bozza appartiene a un run Kind=Document e la sua revisione è quella di input del run: InputRevisionId è NULL per le
            // Analysis, quindi nessuna di loro può essere referenziata. Scritta a mano perché EF non ammette una colonna nullable in una chiave.
            migrationBuilder.Sql(
                """
                ALTER TABLE "Drafts" ADD CONSTRAINT "FK_Drafts_ProcessingRuns_ProcessingRunId_AnalysisRevisionId"
                    FOREIGN KEY ("ProcessingRunId", "AnalysisRevisionId") REFERENCES "ProcessingRuns" ("Id", "InputRevisionId") ON DELETE RESTRICT;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessingRuns_Drafts_DraftId",
                table: "ProcessingRuns",
                column: "DraftId",
                principalTable: "Drafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Drafts_LatestDraftId",
                table: "Projects",
                column: "LatestDraftId",
                principalTable: "Drafts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProcessingRuns_Drafts_DraftId",
                table: "ProcessingRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Drafts_LatestDraftId",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "Drafts");

            migrationBuilder.DropIndex(
                name: "IX_Projects_LatestDraftId",
                table: "Projects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProcessingRuns_Id_ProjectId",
                table: "ProcessingRuns");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingRuns_DraftId",
                table: "ProcessingRuns");

            migrationBuilder.DropIndex(
                name: "UX_ProcessingRuns_Id_InputRevisionId",
                table: "ProcessingRuns");
        }
    }
}
