using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowCheckpoints_WorkflowCheckpoints_ProcessingRunId_Wor~",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Check~",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Paren~",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProcessingRunId",
                table: "WorkflowCheckpoints",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ChatTurnId",
                table: "WorkflowCheckpoints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentJobId",
                table: "WorkflowCheckpoints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "WorkflowCheckpoints",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DocumentJobId",
                table: "InferenceCalls",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DocumentJobs_Id_ProjectId",
                table: "DocumentJobs",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.CreateTable(
                name: "WorkManifests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RuntimeFingerprint = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DefinitionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AlgorithmVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PlanJson = table.Column<string>(type: "jsonb", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkManifests", x => x.Id);
                    table.UniqueConstraint("AK_WorkManifests_OwnerKind_OwnerId", x => new { x.OwnerKind, x.OwnerId });
                    table.UniqueConstraint("AK_WorkManifests_OwnerKind_OwnerId_Hash", x => new { x.OwnerKind, x.OwnerId, x.Hash });
                    table.CheckConstraint("CK_WorkManifests_Hashes", "\"ConfigHash\" ~ '^[0-9a-f]{64}$' AND \"SourceHash\" ~ '^[0-9a-f]{64}$' AND \"PolicyHash\" ~ '^[0-9a-f]{64}$' AND \"Hash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_WorkManifests_Plan", "jsonb_typeof(\"PlanJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_WorkManifests_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DerivedBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    View = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Pass = table.Column<int>(type: "integer", nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: true),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    ParentOutputHashes = table.Column<string>(type: "jsonb", nullable: false),
                    InputIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    MeasuredTokens = table.Column<int>(type: "integer", nullable: true),
                    TokenProvenance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedBatches", x => x.Id);
                    table.UniqueConstraint("AK_DerivedBatches_OwnerKind_OwnerId_BatchId", x => new { x.OwnerKind, x.OwnerId, x.BatchId });
                    table.UniqueConstraint("AK_DerivedBatches_OwnerKind_OwnerId_BatchId_InputHash", x => new { x.OwnerKind, x.OwnerId, x.BatchId, x.InputHash });
                    table.CheckConstraint("CK_DerivedBatches_InputHash", "\"InputHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_DerivedBatches_Numbers", "\"Ordinal\" >= 0 AND (\"Pass\" IS NULL OR \"Pass\" >= 0) AND (\"Level\" IS NULL OR \"Level\" >= 0) AND (\"MeasuredTokens\" IS NULL OR \"MeasuredTokens\" >= 0)");
                    table.CheckConstraint("CK_DerivedBatches_Parents", "jsonb_typeof(\"ParentOutputHashes\") = 'array'");
                    table.CheckConstraint("CK_DerivedBatches_TokenProvenance", "\"TokenProvenance\" IN ('measured', 'calibrated_upper_bound', 'unavailable')");
                    table.ForeignKey(
                        name: "FK_DerivedBatches_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DerivedBatches_WorkManifests_OwnerKind_OwnerId",
                        columns: x => new { x.OwnerKind, x.OwnerId },
                        principalTable: "WorkManifests",
                        principalColumns: new[] { "OwnerKind", "OwnerId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkOutputs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PayloadByteLength = table.Column<long>(type: "bigint", nullable: true),
                    CoveredIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    MapsJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExpansionReceiptJson = table.Column<string>(type: "jsonb", nullable: true),
                    SchemaVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValidationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CommittedFence = table.Column<long>(type: "bigint", nullable: false),
                    DerivedFromOutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkOutputs", x => x.Id);
                    table.CheckConstraint("CK_WorkOutputs_Fence", "\"CommittedFence\" >= 0 AND (\"PayloadByteLength\" IS NULL OR \"PayloadByteLength\" >= 0)");
                    table.CheckConstraint("CK_WorkOutputs_Hashes", "\"InputHash\" ~ '^[0-9a-f]{64}$' AND \"OutputHash\" ~ '^[0-9a-f]{64}$' AND \"ManifestHash\" ~ '^[0-9a-f]{64}$' AND (\"DerivedFromOutputHash\" IS NULL OR \"DerivedFromOutputHash\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_WorkOutputs_Payload", "(\"PayloadJson\" IS NOT NULL)::int + (\"StorageKey\" IS NOT NULL)::int = 1 AND (\"StorageKey\" IS NULL) = (\"PayloadByteLength\" IS NULL)");
                    table.CheckConstraint("CK_WorkOutputs_Status", "\"Status\" = 'Committed'");
                    table.ForeignKey(
                        name: "FK_WorkOutputs_DerivedBatches_OwnerKind_OwnerId_BatchId_InputH~",
                        columns: x => new { x.OwnerKind, x.OwnerId, x.BatchId, x.InputHash },
                        principalTable: "DerivedBatches",
                        principalColumns: new[] { "OwnerKind", "OwnerId", "BatchId", "InputHash" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOutputs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkOutputs_WorkManifests_OwnerKind_OwnerId_ManifestHash",
                        columns: x => new { x.OwnerKind, x.OwnerId, x.ManifestHash },
                        principalTable: "WorkManifests",
                        principalColumns: new[] { "OwnerKind", "OwnerId", "Hash" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_ChatTurnId_WorkflowSessionId_Checkpoint~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ChatTurnId", "WorkflowSessionId", "CheckpointKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_DocumentJobId_ProjectId",
                table: "WorkflowCheckpoints",
                columns: new[] { "DocumentJobId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_DocumentJobId_WorkflowSessionId_Checkpo~",
                table: "WorkflowCheckpoints",
                columns: new[] { "DocumentJobId", "WorkflowSessionId", "CheckpointKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_Job_OldestFirst",
                table: "WorkflowCheckpoints",
                columns: new[] { "DocumentJobId", "CommitSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_ProjectId",
                table: "WorkflowCheckpoints",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_Turn_OldestFirst",
                table: "WorkflowCheckpoints",
                columns: new[] { "ChatTurnId", "CommitSequence" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowCheckpoints_Owner",
                table: "WorkflowCheckpoints",
                sql: "(\"ProcessingRunId\" IS NOT NULL)::int + (\"ChatTurnId\" IS NOT NULL)::int + (\"DocumentJobId\" IS NOT NULL)::int = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkflowCheckpoints_Project",
                table: "WorkflowCheckpoints",
                sql: "\"ProcessingRunId\" IS NOT NULL OR \"ProjectId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_DocumentJobId_Attempt",
                table: "InferenceCalls",
                columns: new[] { "DocumentJobId", "Attempt" });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_DocumentJobId_ProjectId",
                table: "InferenceCalls",
                columns: new[] { "DocumentJobId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_InferenceCalls_OneFinalPerDocumentJob",
                table: "InferenceCalls",
                column: "DocumentJobId",
                unique: true,
                filter: "\"IsFinal\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls",
                sql: "(\"ChatTurnId\" IS NOT NULL)::int + (\"ProcessingRunId\" IS NOT NULL)::int + (\"EvidenceExtractionJobId\" IS NOT NULL)::int + (\"DocumentJobId\" IS NOT NULL)::int = 1");

            migrationBuilder.CreateIndex(
                name: "IX_DerivedBatches_OwnerKind_OwnerId_Ordinal",
                table: "DerivedBatches",
                columns: new[] { "OwnerKind", "OwnerId", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_DerivedBatches_ProjectId",
                table: "DerivedBatches",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkManifests_ProjectId",
                table: "WorkManifests",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOutputs_OwnerKind_OwnerId_BatchId_InputHash",
                table: "WorkOutputs",
                columns: new[] { "OwnerKind", "OwnerId", "BatchId", "InputHash" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOutputs_OwnerKind_OwnerId_ManifestHash",
                table: "WorkOutputs",
                columns: new[] { "OwnerKind", "OwnerId", "ManifestHash" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOutputs_OwnerKind_OwnerId_OutputHash",
                table: "WorkOutputs",
                columns: new[] { "OwnerKind", "OwnerId", "OutputHash" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkOutputs_ProjectId",
                table: "WorkOutputs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkOutputs_StorageKey",
                table: "WorkOutputs",
                column: "StorageKey",
                filter: "\"StorageKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkOutputs_OwnerBatch",
                table: "WorkOutputs",
                columns: new[] { "OwnerKind", "OwnerId", "BatchId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_InferenceCalls_DocumentJobs_DocumentJobId_ProjectId",
                table: "InferenceCalls",
                columns: new[] { "DocumentJobId", "ProjectId" },
                principalTable: "DocumentJobs",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowCheckpoints_ChatTurns_ChatTurnId",
                table: "WorkflowCheckpoints",
                column: "ChatTurnId",
                principalTable: "ChatTurns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowCheckpoints_DocumentJobs_DocumentJobId_ProjectId",
                table: "WorkflowCheckpoints",
                columns: new[] { "DocumentJobId", "ProjectId" },
                principalTable: "DocumentJobs",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowCheckpoints_Projects_ProjectId",
                table: "WorkflowCheckpoints",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Parent del checkpoint: stesso owner e stessa sessione. EF non modella una chiave alternativa su colonne nullable, quindi le FK
            // dei tre owner (ProcessingRun 001 incluso: la FK storica su AK è sostituita) referenziano gli indici unici (owner, sessione, chiave).
            foreach (var column in new[] { "ProcessingRunId", "ChatTurnId", "DocumentJobId" })
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE \"WorkflowCheckpoints\" ADD CONSTRAINT \"FK_WorkflowCheckpoints_Parent_{column}\" " +
                    $"FOREIGN KEY (\"{column}\", \"WorkflowSessionId\", \"ParentCheckpointKey\") " +
                    $"REFERENCES \"WorkflowCheckpoints\" (\"{column}\", \"WorkflowSessionId\", \"CheckpointKey\") ON DELETE RESTRICT");
                migrationBuilder.Sql($"CREATE INDEX \"IX_WorkflowCheckpoints_Parent_{column}\" ON \"WorkflowCheckpoints\" (\"{column}\", \"WorkflowSessionId\", \"ParentCheckpointKey\")");
            }

            // Dati di lavoro append-only: nessun UPDATE né DELETE; l'hash discordante è un errore d'integrità, mai un aggiornamento.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION "bprojects_forbid_work_mutation"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'La tabella % è append-only: % non ammesso.', TG_TABLE_NAME, TG_OP USING ERRCODE = 'restrict_violation';
                END
                $$
                """);
            foreach (var table in new[] { "WorkManifests", "DerivedBatches", "WorkOutputs" })
            {
                migrationBuilder.Sql(
                    $"CREATE TRIGGER \"TR_{table}_AppendOnly\" BEFORE UPDATE OR DELETE ON \"{table}\" FOR EACH ROW EXECUTE FUNCTION \"bprojects_forbid_work_mutation\"()");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in new[] { "WorkManifests", "DerivedBatches", "WorkOutputs" })
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS \"TR_{table}_AppendOnly\" ON \"{table}\"");
            }

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS \"bprojects_forbid_work_mutation\"()");
            foreach (var column in new[] { "ProcessingRunId", "ChatTurnId", "DocumentJobId" })
            {
                migrationBuilder.Sql($"ALTER TABLE \"WorkflowCheckpoints\" DROP CONSTRAINT IF EXISTS \"FK_WorkflowCheckpoints_Parent_{column}\"");
                migrationBuilder.Sql($"DROP INDEX IF EXISTS \"IX_WorkflowCheckpoints_Parent_{column}\"");
            }

            migrationBuilder.DropForeignKey(
                name: "FK_InferenceCalls_DocumentJobs_DocumentJobId_ProjectId",
                table: "InferenceCalls");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowCheckpoints_ChatTurns_ChatTurnId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowCheckpoints_DocumentJobs_DocumentJobId_ProjectId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropForeignKey(
                name: "FK_WorkflowCheckpoints_Projects_ProjectId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropTable(
                name: "WorkOutputs");

            migrationBuilder.DropTable(
                name: "DerivedBatches");

            migrationBuilder.DropTable(
                name: "WorkManifests");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_ChatTurnId_WorkflowSessionId_Checkpoint~",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_DocumentJobId_ProjectId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_DocumentJobId_WorkflowSessionId_Checkpo~",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_Job_OldestFirst",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_ProjectId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowCheckpoints_Turn_OldestFirst",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowCheckpoints_Owner",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkflowCheckpoints_Project",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropIndex(
                name: "IX_InferenceCalls_DocumentJobId_Attempt",
                table: "InferenceCalls");

            migrationBuilder.DropIndex(
                name: "IX_InferenceCalls_DocumentJobId_ProjectId",
                table: "InferenceCalls");

            migrationBuilder.DropIndex(
                name: "UX_InferenceCalls_OneFinalPerDocumentJob",
                table: "InferenceCalls");

            migrationBuilder.DropCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DocumentJobs_Id_ProjectId",
                table: "DocumentJobs");

            migrationBuilder.DropColumn(
                name: "ChatTurnId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropColumn(
                name: "DocumentJobId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "WorkflowCheckpoints");

            migrationBuilder.DropColumn(
                name: "DocumentJobId",
                table: "InferenceCalls");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProcessingRunId",
                table: "WorkflowCheckpoints",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Check~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "WorkflowSessionId", "CheckpointKey" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Paren~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "WorkflowSessionId", "ParentCheckpointKey" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_InferenceCalls_Owner",
                table: "InferenceCalls",
                sql: "(\"ChatTurnId\" IS NOT NULL)::int + (\"ProcessingRunId\" IS NOT NULL)::int + (\"EvidenceExtractionJobId\" IS NOT NULL)::int = 1");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkflowCheckpoints_WorkflowCheckpoints_ProcessingRunId_Wor~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "WorkflowSessionId", "ParentCheckpointKey" },
                principalTable: "WorkflowCheckpoints",
                principalColumns: new[] { "ProcessingRunId", "WorkflowSessionId", "CheckpointKey" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
