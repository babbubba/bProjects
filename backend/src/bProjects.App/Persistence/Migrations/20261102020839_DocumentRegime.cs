using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentRegime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CallCount",
                table: "ProcessingRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationReason",
                table: "ProcessingRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationStatus",
                table: "ProcessingRuns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationVersion",
                table: "ProcessingRuns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EffectiveDeadlineAt",
                table: "ProcessingRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HardDeadlineAt",
                table: "ProcessingRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitialRegime",
                table: "ProcessingRuns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OrdinaryDeadlineAt",
                table: "ProcessingRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyHash",
                table: "ProcessingRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PromotedAt",
                table: "ProcessingRuns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotedReason",
                table: "ProcessingRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotionCount",
                table: "ProcessingRuns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Regime",
                table: "ProcessingRuns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfId",
                table: "ProcessingRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationReason",
                table: "DocumentPreparations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationStatus",
                table: "DocumentPreparations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationVersion",
                table: "DocumentPreparations",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitialRegime",
                table: "DocumentPreparations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OrdinaryDeadlineAt",
                table: "DocumentPreparations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PromotedAt",
                table: "DocumentPreparations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotedReason",
                table: "DocumentPreparations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotionCount",
                table: "DocumentPreparations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Regime",
                table: "DocumentPreparations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CallCount",
                table: "ChatTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationReason",
                table: "ChatTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationStatus",
                table: "ChatTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClassificationVersion",
                table: "ChatTurns",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EffectiveDeadlineAt",
                table: "ChatTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HardDeadlineAt",
                table: "ChatTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InitialRegime",
                table: "ChatTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OrdinaryDeadlineAt",
                table: "ChatTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyHash",
                table: "ChatTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PromotedAt",
                table: "ChatTurns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PromotedReason",
                table: "ChatTurns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PromotionCount",
                table: "ChatTurns",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Regime",
                table: "ChatTurns",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RetryOfId",
                table: "ChatTurns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ClassificationRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuntimeFingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WindowTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    MarginTokens = table.Column<int>(type: "integer", nullable: true),
                    InputBudgetTokens = table.Column<int>(type: "integer", nullable: true),
                    PayloadTokens = table.Column<int>(type: "integer", nullable: true),
                    TokenQuality = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProvenanceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ShapeFloorTokens = table.Column<int>(type: "integer", nullable: true),
                    ShapeSkeletonHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VisibleReserveTokens = table.Column<int>(type: "integer", nullable: true),
                    PlannedCalls = table.Column<int>(type: "integer", nullable: true),
                    UnknownReasonsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassificationRecords", x => x.Id);
                    table.CheckConstraint("CK_ClassificationRecords_Hashes", "\"InputHash\" ~ '^[0-9a-f]{64}$' AND \"PolicyHash\" ~ '^[0-9a-f]{64}$' AND (\"ShapeSkeletonHash\" IS NULL OR \"ShapeSkeletonHash\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_ClassificationRecords_Json", "jsonb_typeof(\"ProvenanceJson\") = 'object' AND jsonb_typeof(\"UnknownReasonsJson\") = 'array'");
                    table.CheckConstraint("CK_ClassificationRecords_Numbers", "(\"PlannedCalls\" IS NULL OR \"PlannedCalls\" >= 0) AND (\"PayloadTokens\" IS NULL OR \"PayloadTokens\" >= 0) AND (\"ShapeFloorTokens\" IS NULL OR \"ShapeFloorTokens\" >= 0)");
                    table.CheckConstraint("CK_ClassificationRecords_OrdinaryOneCall", "\"Outcome\" <> 'AdmitOrdinary' OR \"PlannedCalls\" = 1");
                    table.CheckConstraint("CK_ClassificationRecords_Outcome", "\"Outcome\" IN ('AdmitOrdinary', 'AdmitExtended', 'Reject', 'Fail')");
                    table.CheckConstraint("CK_ClassificationRecords_Owner", "\"OwnerKind\" IN ('ProcessingRun', 'ChatTurn', 'DocumentPreparation') AND \"Ordinal\" >= 1");
                    table.CheckConstraint("CK_ClassificationRecords_TokenQuality", "\"TokenQuality\" IS NULL OR \"TokenQuality\" IN ('measured', 'calibrated_upper_bound', 'unavailable')");
                    table.ForeignKey(
                        name: "FK_ClassificationRecords_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_RetryOfId",
                table: "ProcessingRuns",
                column: "RetryOfId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_CallCount",
                table: "ProcessingRuns",
                sql: "\"CallCount\" IS NULL OR \"CallCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_Cap",
                table: "ProcessingRuns",
                sql: "(\"HardDeadlineAt\" IS NULL AND \"EffectiveDeadlineAt\" IS NULL) OR (\"HardDeadlineAt\" IS NOT NULL AND \"EffectiveDeadlineAt\" IS NOT NULL AND \"EffectiveDeadlineAt\" <= \"HardDeadlineAt\" AND \"HardDeadlineAt\" > \"RequestedAt\" AND (\"OrdinaryDeadlineAt\" IS NULL OR \"OrdinaryDeadlineAt\" <= \"HardDeadlineAt\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_Classification",
                table: "ProcessingRuns",
                sql: "\"ClassificationStatus\" IS NULL OR (\"ClassificationStatus\" = 'Pending' AND \"InitialRegime\" IS NULL AND \"Regime\" = 'Extended') OR (\"ClassificationStatus\" = 'Classified' AND \"InitialRegime\" IS NOT NULL AND \"Regime\" IS NOT NULL) OR (\"ClassificationStatus\" = 'Rejected' AND \"InitialRegime\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_DeadlineIsEffective",
                table: "ProcessingRuns",
                sql: "\"EffectiveDeadlineAt\" IS NULL OR \"DeadlineAt\" = \"EffectiveDeadlineAt\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_NoDowngrade",
                table: "ProcessingRuns",
                sql: "\"Regime\" IS DISTINCT FROM 'Ordinary' OR (\"InitialRegime\" = 'Ordinary' AND COALESCE(\"PromotionCount\", 0) = 0 AND \"OrdinaryDeadlineAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_PolicyHash",
                table: "ProcessingRuns",
                sql: "\"PolicyHash\" IS NULL OR \"PolicyHash\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_Promotion",
                table: "ProcessingRuns",
                sql: "(\"PromotionCount\" IS NULL OR \"PromotionCount\" IN (0, 1)) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedAt\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedReason\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 0 OR (\"InitialRegime\" = 'Ordinary' AND \"Regime\" = 'Extended' AND \"PromotedReason\" = 'additional_work'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_Regime",
                table: "ProcessingRuns",
                sql: "(\"InitialRegime\" IS NULL OR \"InitialRegime\" IN ('Ordinary', 'Extended')) AND (\"Regime\" IS NULL OR \"Regime\" IN ('Ordinary', 'Extended')) AND (\"ClassificationStatus\" IS NULL OR \"ClassificationStatus\" IN ('Pending', 'Classified', 'Rejected'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProcessingRuns_RegimeFields",
                table: "ProcessingRuns",
                sql: "\"ClassificationStatus\" IS NULL OR \"EffectiveDeadlineAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentPreparations_Classification",
                table: "DocumentPreparations",
                sql: "\"ClassificationStatus\" IS NULL OR (\"ClassificationStatus\" = 'Pending' AND \"InitialRegime\" IS NULL AND \"Regime\" = 'Extended') OR (\"ClassificationStatus\" = 'Classified' AND \"InitialRegime\" IS NOT NULL AND \"Regime\" IS NOT NULL) OR (\"ClassificationStatus\" = 'Rejected' AND \"InitialRegime\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentPreparations_NoDowngrade",
                table: "DocumentPreparations",
                sql: "\"Regime\" IS DISTINCT FROM 'Ordinary' OR (\"InitialRegime\" = 'Ordinary' AND COALESCE(\"PromotionCount\", 0) = 0 AND \"OrdinaryDeadlineAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentPreparations_Promotion",
                table: "DocumentPreparations",
                sql: "(\"PromotionCount\" IS NULL OR \"PromotionCount\" IN (0, 1)) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedAt\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedReason\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 0 OR (\"InitialRegime\" = 'Ordinary' AND \"Regime\" = 'Extended' AND \"PromotedReason\" = 'additional_work'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentPreparations_Regime",
                table: "DocumentPreparations",
                sql: "(\"InitialRegime\" IS NULL OR \"InitialRegime\" IN ('Ordinary', 'Extended')) AND (\"Regime\" IS NULL OR \"Regime\" IN ('Ordinary', 'Extended')) AND (\"ClassificationStatus\" IS NULL OR \"ClassificationStatus\" IN ('Pending', 'Classified', 'Rejected'))");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_RetryOfId",
                table: "ChatTurns",
                column: "RetryOfId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_CallCount",
                table: "ChatTurns",
                sql: "\"CallCount\" IS NULL OR \"CallCount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_Cap",
                table: "ChatTurns",
                sql: "(\"HardDeadlineAt\" IS NULL AND \"EffectiveDeadlineAt\" IS NULL) OR (\"HardDeadlineAt\" IS NOT NULL AND \"EffectiveDeadlineAt\" IS NOT NULL AND \"EffectiveDeadlineAt\" <= \"HardDeadlineAt\" AND \"HardDeadlineAt\" > \"RequestedAt\" AND (\"OrdinaryDeadlineAt\" IS NULL OR \"OrdinaryDeadlineAt\" <= \"HardDeadlineAt\"))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_Classification",
                table: "ChatTurns",
                sql: "\"ClassificationStatus\" IS NULL OR (\"ClassificationStatus\" = 'Pending' AND \"InitialRegime\" IS NULL AND \"Regime\" = 'Extended') OR (\"ClassificationStatus\" = 'Classified' AND \"InitialRegime\" IS NOT NULL AND \"Regime\" IS NOT NULL) OR (\"ClassificationStatus\" = 'Rejected' AND \"InitialRegime\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_DeadlineIsEffective",
                table: "ChatTurns",
                sql: "\"EffectiveDeadlineAt\" IS NULL OR \"DeadlineAt\" = \"EffectiveDeadlineAt\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_NoDowngrade",
                table: "ChatTurns",
                sql: "\"Regime\" IS DISTINCT FROM 'Ordinary' OR (\"InitialRegime\" = 'Ordinary' AND COALESCE(\"PromotionCount\", 0) = 0 AND \"OrdinaryDeadlineAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_PolicyHash",
                table: "ChatTurns",
                sql: "\"PolicyHash\" IS NULL OR \"PolicyHash\" ~ '^[0-9a-f]{64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_Promotion",
                table: "ChatTurns",
                sql: "(\"PromotionCount\" IS NULL OR \"PromotionCount\" IN (0, 1)) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedAt\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 1) = (\"PromotedReason\" IS NOT NULL) AND (COALESCE(\"PromotionCount\", 0) = 0 OR (\"InitialRegime\" = 'Ordinary' AND \"Regime\" = 'Extended' AND \"PromotedReason\" = 'additional_work'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_Regime",
                table: "ChatTurns",
                sql: "(\"InitialRegime\" IS NULL OR \"InitialRegime\" IN ('Ordinary', 'Extended')) AND (\"Regime\" IS NULL OR \"Regime\" IN ('Ordinary', 'Extended')) AND (\"ClassificationStatus\" IS NULL OR \"ClassificationStatus\" IN ('Pending', 'Classified', 'Rejected'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatTurns_RegimeFields",
                table: "ChatTurns",
                sql: "\"ClassificationStatus\" IS NULL OR \"EffectiveDeadlineAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassificationRecords_ProjectId",
                table: "ClassificationRecords",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "UX_ClassificationRecords_OwnerOrdinal",
                table: "ClassificationRecords",
                columns: new[] { "OwnerKind", "OwnerId", "Ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatTurns_ChatTurns_RetryOfId",
                table: "ChatTurns",
                column: "RetryOfId",
                principalTable: "ChatTurns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessingRuns_ProcessingRuns_RetryOfId",
                table: "ProcessingRuns",
                column: "RetryOfId",
                principalTable: "ProcessingRuns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ClassificationRecord append-only: stessa funzione dei dati di lavoro (migrazione DocumentWork); il DROP TABLE della Down elimina il trigger.
            migrationBuilder.Sql(
                "CREATE TRIGGER \"TR_ClassificationRecords_AppendOnly\" BEFORE UPDATE OR DELETE ON \"ClassificationRecords\" FOR EACH ROW EXECUTE FUNCTION \"bprojects_forbid_work_mutation\"()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ChatTurns_ChatTurns_RetryOfId",
                table: "ChatTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_ProcessingRuns_ProcessingRuns_RetryOfId",
                table: "ProcessingRuns");

            migrationBuilder.DropTable(
                name: "ClassificationRecords");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingRuns_RetryOfId",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_CallCount",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_Cap",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_Classification",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_DeadlineIsEffective",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_NoDowngrade",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_PolicyHash",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_Promotion",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_Regime",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProcessingRuns_RegimeFields",
                table: "ProcessingRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentPreparations_Classification",
                table: "DocumentPreparations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentPreparations_NoDowngrade",
                table: "DocumentPreparations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentPreparations_Promotion",
                table: "DocumentPreparations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentPreparations_Regime",
                table: "DocumentPreparations");

            migrationBuilder.DropIndex(
                name: "IX_ChatTurns_RetryOfId",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_CallCount",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_Cap",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_Classification",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_DeadlineIsEffective",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_NoDowngrade",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_PolicyHash",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_Promotion",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_Regime",
                table: "ChatTurns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatTurns_RegimeFields",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "CallCount",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "ClassificationReason",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "ClassificationStatus",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "ClassificationVersion",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "EffectiveDeadlineAt",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "HardDeadlineAt",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "InitialRegime",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "OrdinaryDeadlineAt",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "PolicyHash",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "PromotedAt",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "PromotedReason",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "PromotionCount",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "Regime",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "RetryOfId",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "ClassificationReason",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "ClassificationStatus",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "ClassificationVersion",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "InitialRegime",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "OrdinaryDeadlineAt",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "PromotedAt",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "PromotedReason",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "PromotionCount",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "Regime",
                table: "DocumentPreparations");

            migrationBuilder.DropColumn(
                name: "CallCount",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "ClassificationReason",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "ClassificationStatus",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "ClassificationVersion",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "EffectiveDeadlineAt",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "HardDeadlineAt",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "InitialRegime",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "OrdinaryDeadlineAt",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "PolicyHash",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "PromotedAt",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "PromotedReason",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "PromotionCount",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "Regime",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "RetryOfId",
                table: "ChatTurns");
        }
    }
}
