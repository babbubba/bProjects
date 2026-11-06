using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentLimitsSnapshot : Migration
    {
        private const string Check =
            "\"Kind\" <> 'Analysis' OR (\"ClassificationStatus\" IS NULL AND \"WorkflowDefinitionVersion\" NOT LIKE 'project-analysis-documents-%') " +
            "OR COALESCE(jsonb_typeof(\"DocumentLimitsSnapshot\") = 'object' AND jsonb_typeof(\"DocumentLimitsSnapshot\"->'limits') = 'object' " +
            "AND (\"DocumentLimitsSnapshot\"->>'schemaVersion') ~ '^[0-9]+$' AND (\"DocumentLimitsSnapshot\"->>'policyRevision') ~ '^[0-9]+$' " +
            "AND (\"DocumentLimitsSnapshot\"->>'hash') ~ '^[0-9a-f]{64}$', false)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentLimitsSnapshot",
                table: "ProcessingRuns",
                type: "jsonb",
                nullable: true);

            // Nessun backfill né marcatore legacy: i run documentali di Analizza già presenti, privi di snapshot, fanno fallire esplicitamente questa migrazione.
            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_DocumentLimitsSnapshot",
                table: "ProcessingRuns",
                sql: Check);

            // Immutabilità dello snapshot: solo la colonna protetta (WHEN ... IS DISTINCT FROM); stato, lease, fencing, contatori e checkpoint si aggiornano liberamente.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "bprojects_forbid_run_limits_snapshot_change"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."DocumentLimitsSnapshot" IS DISTINCT FROM OLD."DocumentLimitsSnapshot" THEN
                        RAISE EXCEPTION 'ProcessingRuns.DocumentLimitsSnapshot è immutabile: scrittura o modifica non ammessa.' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END
                $$
                """);
            migrationBuilder.Sql(
                """
                CREATE TRIGGER "TR_ProcessingRuns_DocumentLimitsSnapshot" BEFORE UPDATE ON "ProcessingRuns" FOR EACH ROW
                WHEN (OLD."DocumentLimitsSnapshot" IS DISTINCT FROM NEW."DocumentLimitsSnapshot")
                EXECUTE FUNCTION "bprojects_forbid_run_limits_snapshot_change"()
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_ProcessingRuns_DocumentLimitsSnapshot\" ON \"ProcessingRuns\"");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS \"bprojects_forbid_run_limits_snapshot_change\"()");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_DocumentLimitsSnapshot",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "DocumentLimitsSnapshot",
                table: "ProcessingRuns");
        }
    }
}