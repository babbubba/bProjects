using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentRegimeTransition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegimeTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromRegime = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ToRegime = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OldDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NewDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ElapsedMs = table.Column<long>(type: "bigint", nullable: false),
                    PolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    ReceiptJson = table.Column<string>(type: "jsonb", nullable: false),
                    DerivedPlanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegimeTransitions", x => x.Id);
                    table.CheckConstraint("CK_RegimeTransitions_Deadlines", "\"NewDeadlineAt\" > \"OldDeadlineAt\" AND \"ElapsedMs\" >= 0 AND \"Fence\" >= 0");
                    table.CheckConstraint("CK_RegimeTransitions_Hashes", "\"PolicyHash\" ~ '^[0-9a-f]{64}$' AND \"DerivedPlanHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RegimeTransitions_Promotion", "\"FromRegime\" = 'Ordinary' AND \"ToRegime\" = 'Extended' AND \"Reason\" = 'additional_work'");
                    table.CheckConstraint("CK_RegimeTransitions_Receipt", "jsonb_typeof(\"ReceiptJson\") = 'object'");
                    table.CheckConstraint("CK_RegimeTransitions_Root", "\"RootKind\" IN ('ProcessingRun', 'ChatTurn')");
                    table.ForeignKey(
                        name: "FK_RegimeTransitions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegimeTransitions_ProjectId",
                table: "RegimeTransitions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "UX_RegimeTransitions_RootTo",
                table: "RegimeTransitions",
                columns: new[] { "RootKind", "RootId", "ToRegime" },
                unique: true);

            // Append-only: stessa funzione dei dati di lavoro (migrazione DocumentWork); il DROP TABLE della Down elimina il trigger.
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_RegimeTransitions_AppendOnly\" BEFORE UPDATE OR DELETE ON \"RegimeTransitions\" FOR EACH ROW EXECUTE FUNCTION \"bprojects_forbid_work_mutation\"()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegimeTransitions");
        }
    }
}
