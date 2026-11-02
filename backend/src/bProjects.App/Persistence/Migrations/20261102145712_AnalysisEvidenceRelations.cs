using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalysisEvidenceRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisEvidenceRelations",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Basis = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisEvidenceRelations", x => new { x.RunId, x.FromEvidenceId, x.ToEvidenceId, x.Type });
                    table.CheckConstraint("CK_AnalysisEvidenceRelations_NotSelf", "\"FromEvidenceId\" <> \"ToEvidenceId\"");
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceRelations_AnalysisEvidenceSelections_RunId",
                        column: x => x.RunId,
                        principalTable: "AnalysisEvidenceSelections",
                        principalColumn: "RunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceRelations_Evidences_FromEvidenceId",
                        column: x => x.FromEvidenceId,
                        principalTable: "Evidences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceRelations_Evidences_ToEvidenceId",
                        column: x => x.ToEvidenceId,
                        principalTable: "Evidences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AnalysisEvidenceRelations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceRelations_FromEvidenceId",
                table: "AnalysisEvidenceRelations",
                column: "FromEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceRelations_ProjectId",
                table: "AnalysisEvidenceRelations",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceRelations_ToEvidenceId",
                table: "AnalysisEvidenceRelations",
                column: "ToEvidenceId");

            // Relazioni interpretative del run append-only (stessa funzione dei dati di lavoro): nessun UPDATE né DELETE.
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_AnalysisEvidenceRelations_AppendOnly\" BEFORE UPDATE OR DELETE ON \"AnalysisEvidenceRelations\" FOR EACH ROW EXECUTE FUNCTION \"bprojects_forbid_work_mutation\"()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_AnalysisEvidenceRelations_AppendOnly\" ON \"AnalysisEvidenceRelations\"");

            migrationBuilder.DropTable(
                name: "AnalysisEvidenceRelations");
        }
    }
}
