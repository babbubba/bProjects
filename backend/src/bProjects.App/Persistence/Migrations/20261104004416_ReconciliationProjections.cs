using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReconciliationProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunCandidates",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProposedType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCandidates", x => new { x.RunId, x.CandidateId });
                    table.CheckConstraint("CK_RunCandidates_Hashes", "\"CandidateId\" ~ '^[0-9a-f]{64}$' AND \"SourceHash\" ~ '^[0-9a-f]{64}$' AND \"InputHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RunCandidates_Payload", "jsonb_typeof(\"PayloadJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_RunCandidates_ProcessingRuns_RunId_ProjectId",
                        columns: x => new { x.RunId, x.ProjectId },
                        principalTable: "ProcessingRuns",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCatalogNodes",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BatchId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    View = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    MemberSetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false),
                    DescriptorOutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCatalogNodes", x => new { x.RunId, x.NodeId });
                    table.UniqueConstraint("AK_RunCatalogNodes_RunId_NodeId_View_Level", x => new { x.RunId, x.NodeId, x.View, x.Level });
                    table.CheckConstraint("CK_RunCatalogNodes_Hashes", "\"NodeId\" ~ '^[0-9a-f]{64}$' AND \"MemberSetHash\" ~ '^[0-9a-f]{64}$' AND \"DescriptorOutputHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RunCatalogNodes_Numbers", "\"Level\" >= 0 AND \"Ordinal\" >= 0 AND \"MemberCount\" >= 1 AND \"View\" IN ('A', 'B', 'Cross') AND \"OwnerKind\" = 'ProcessingRun'");
                    table.CheckConstraint("CK_RunCatalogNodes_Payload", "jsonb_typeof(\"PayloadJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_RunCatalogNodes_DerivedBatches_OwnerKind_RunId_BatchId",
                        columns: x => new { x.OwnerKind, x.RunId, x.BatchId },
                        principalTable: "DerivedBatches",
                        principalColumns: new[] { "OwnerKind", "OwnerId", "BatchId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunCatalogNodes_ProcessingRuns_RunId_ProjectId",
                        columns: x => new { x.RunId, x.ProjectId },
                        principalTable: "ProcessingRuns",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCatalogSets",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    View = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    MemberSetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCatalogSets", x => new { x.RunId, x.SetId });
                    table.CheckConstraint("CK_RunCatalogSets_Hashes", "\"SetId\" ~ '^[0-9a-f]{64}$' AND \"MemberSetHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RunCatalogSets_Numbers", "\"MemberCount\" >= 1 AND \"View\" IN ('A', 'B', 'Cross')");
                    table.ForeignKey(
                        name: "FK_RunCatalogSets_ProcessingRuns_RunId_ProjectId",
                        columns: x => new { x.RunId, x.ProjectId },
                        principalTable: "ProcessingRuns",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunPatches",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaseHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpectedHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    OriginOutputHashesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunPatches", x => new { x.RunId, x.PatchId });
                    table.CheckConstraint("CK_RunPatches_Hashes", "\"PatchId\" ~ '^[0-9a-f]{64}$' AND \"SourceHash\" ~ '^[0-9a-f]{64}$' AND \"BaseHash\" ~ '^[0-9a-f]{64}$' AND \"ExpectedHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RunPatches_Payload", "jsonb_typeof(\"PayloadJson\") = 'object' AND jsonb_typeof(\"OriginOutputHashesJson\") = 'array' AND jsonb_array_length(\"OriginOutputHashesJson\") > 0");
                    table.ForeignKey(
                        name: "FK_RunPatches_ProcessingRuns_RunId_ProjectId",
                        columns: x => new { x.RunId, x.ProjectId },
                        principalTable: "ProcessingRuns",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCandidateDispositions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChildCandidateIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ComparisonOutputHashesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCandidateDispositions", x => new { x.RunId, x.CandidateId, x.Version });
                    table.CheckConstraint("CK_RunCandidateDispositions_Shape", "\"Version\" >= 1 AND \"State\" IN ('Pending', 'Expanded', 'Compared', 'Open', 'NotProcessed') AND jsonb_typeof(\"ChildCandidateIdsJson\") = 'array' AND jsonb_typeof(\"ComparisonOutputHashesJson\") = 'array'");
                    table.CheckConstraint("CK_RunCandidateDispositions_State", "(\"State\" <> 'Expanded' OR jsonb_array_length(\"ChildCandidateIdsJson\") > 0) AND (\"State\" NOT IN ('Compared', 'Open') OR jsonb_array_length(\"ComparisonOutputHashesJson\") > 0) AND (\"State\" NOT IN ('Open', 'NotProcessed') OR (\"Reason\" IS NOT NULL AND length(btrim(\"Reason\")) > 0))");
                    table.ForeignKey(
                        name: "FK_RunCandidateDispositions_RunCandidates_RunId_CandidateId",
                        columns: x => new { x.RunId, x.CandidateId },
                        principalTable: "RunCandidates",
                        principalColumns: new[] { "RunId", "CandidateId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCandidateOrigins",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OriginOutputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCandidateOrigins", x => new { x.RunId, x.CandidateId, x.BatchId });
                    table.CheckConstraint("CK_RunCandidateOrigins_Hash", "\"OriginOutputHash\" ~ '^[0-9a-f]{64}$' AND \"OwnerKind\" = 'ProcessingRun'");
                    table.ForeignKey(
                        name: "FK_RunCandidateOrigins_DerivedBatches_OwnerKind_RunId_BatchId",
                        columns: x => new { x.OwnerKind, x.RunId, x.BatchId },
                        principalTable: "DerivedBatches",
                        principalColumns: new[] { "OwnerKind", "OwnerId", "BatchId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunCandidateOrigins_RunCandidates_RunId_CandidateId",
                        columns: x => new { x.RunId, x.CandidateId },
                        principalTable: "RunCandidates",
                        principalColumns: new[] { "RunId", "CandidateId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCatalogMembers",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    View = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCatalogMembers", x => new { x.RunId, x.NodeId, x.EvidenceId });
                    table.CheckConstraint("CK_RunCatalogMembers_Hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_RunCatalogMembers_Evidences_EvidenceId",
                        column: x => x.EvidenceId,
                        principalTable: "Evidences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunCatalogMembers_RunCatalogNodes_RunId_NodeId_View_Level",
                        columns: x => new { x.RunId, x.NodeId, x.View, x.Level },
                        principalTable: "RunCatalogNodes",
                        principalColumns: new[] { "RunId", "NodeId", "View", "Level" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCatalogPages",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PageHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousPageHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousOrdinal = table.Column<int>(type: "integer", nullable: true, computedColumnSql: "CASE WHEN \"Ordinal\" > 0 THEN \"Ordinal\" - 1 END", stored: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCatalogPages", x => new { x.RunId, x.PageId });
                    table.UniqueConstraint("AK_RunCatalogPages_RunId_NodeId_Kind_Ordinal_PageHash", x => new { x.RunId, x.NodeId, x.Kind, x.Ordinal, x.PageHash });
                    table.CheckConstraint("CK_RunCatalogPages_Chain", "\"Ordinal\" >= 0 AND (\"Ordinal\" = 0) = (\"PreviousPageHash\" IS NULL) AND \"Kind\" IN ('Members', 'Keys', 'Relations')");
                    table.CheckConstraint("CK_RunCatalogPages_Hashes", "\"PageId\" ~ '^[0-9a-f]{64}$' AND \"NodeId\" ~ '^[0-9a-f]{64}$' AND \"PageHash\" ~ '^[0-9a-f]{64}$' AND (\"PreviousPageHash\" IS NULL OR \"PreviousPageHash\" ~ '^[0-9a-f]{64}$')");
                    table.CheckConstraint("CK_RunCatalogPages_Payload", "jsonb_typeof(\"PayloadJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_RunCatalogPages_RunCatalogNodes_RunId_NodeId",
                        columns: x => new { x.RunId, x.NodeId },
                        principalTable: "RunCatalogNodes",
                        principalColumns: new[] { "RunId", "NodeId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunCatalogSetNodes",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunCatalogSetNodes", x => new { x.RunId, x.SetId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_RunCatalogSetNodes_RunCatalogNodes_RunId_NodeId",
                        columns: x => new { x.RunId, x.NodeId },
                        principalTable: "RunCatalogNodes",
                        principalColumns: new[] { "RunId", "NodeId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunCatalogSetNodes_RunCatalogSets_RunId_SetId",
                        columns: x => new { x.RunId, x.SetId },
                        principalTable: "RunCatalogSets",
                        principalColumns: new[] { "RunId", "SetId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RunPatchDispositions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    PatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunPatchDispositions", x => new { x.RunId, x.PatchId, x.Version });
                    table.CheckConstraint("CK_RunPatchDispositions_Reason", "\"Kind\" IN ('Applied', 'Duplicate') OR (\"Reason\" IS NOT NULL AND length(btrim(\"Reason\")) > 0)");
                    table.CheckConstraint("CK_RunPatchDispositions_Shape", "\"Version\" >= 1 AND \"Kind\" IN ('Applied', 'Duplicate', 'PreservedAsConflict', 'Stale', 'Invalid', 'NotProcessed') AND (\"ResultHash\" IS NULL OR \"ResultHash\" ~ '^[0-9a-f]{64}$')");
                    table.ForeignKey(
                        name: "FK_RunPatchDispositions_RunPatches_RunId_PatchId",
                        columns: x => new { x.RunId, x.PatchId },
                        principalTable: "RunPatches",
                        principalColumns: new[] { "RunId", "PatchId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunCandidateOrigins_OwnerKind_RunId_BatchId",
                table: "RunCandidateOrigins",
                columns: new[] { "OwnerKind", "RunId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunCandidates_RunId_ProjectId",
                table: "RunCandidates",
                columns: new[] { "RunId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogMembers_EvidenceId",
                table: "RunCatalogMembers",
                column: "EvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogMembers_RunId_NodeId_View_Level",
                table: "RunCatalogMembers",
                columns: new[] { "RunId", "NodeId", "View", "Level" });

            migrationBuilder.CreateIndex(
                name: "UX_RunCatalogMembers_Level",
                table: "RunCatalogMembers",
                columns: new[] { "RunId", "View", "Level", "EvidenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogNodes_OwnerKind_RunId_BatchId",
                table: "RunCatalogNodes",
                columns: new[] { "OwnerKind", "RunId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogNodes_RunId_ProjectId",
                table: "RunCatalogNodes",
                columns: new[] { "RunId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_RunCatalogNodes_Position",
                table: "RunCatalogNodes",
                columns: new[] { "RunId", "View", "Level", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_RunCatalogPages_Chain",
                table: "RunCatalogPages",
                columns: new[] { "RunId", "NodeId", "Kind", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogSetNodes_RunId_NodeId",
                table: "RunCatalogSetNodes",
                columns: new[] { "RunId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunCatalogSets_RunId_ProjectId",
                table: "RunCatalogSets",
                columns: new[] { "RunId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_RunPatches_RunId_ProjectId",
                table: "RunPatches",
                columns: new[] { "RunId", "ProjectId" });

            // Catena di pagine imposta dal database: la pagina n richiede la n-1 dello stesso (run,nodo,kind) con l'hash citato (nessun buco, nessun hash discordante).
            // EF non modella FK con colonne nullable parziali: con previousPageHash/PreviousOrdinal nulli (prima pagina) la FK non si applica (MATCH SIMPLE).
            migrationBuilder.Sql(
                "ALTER TABLE \"RunCatalogPages\" ADD CONSTRAINT \"FK_RunCatalogPages_Chain\" " +
                "FOREIGN KEY (\"RunId\", \"NodeId\", \"Kind\", \"PreviousOrdinal\", \"PreviousPageHash\") " +
                "REFERENCES \"RunCatalogPages\" (\"RunId\", \"NodeId\", \"Kind\", \"Ordinal\", \"PageHash\") ON DELETE RESTRICT");

            // Proiezioni del run append-only (stessa funzione dei dati di lavoro): nessun UPDATE né DELETE; un payload discordante è un errore d'integrità.
            foreach (var table in new[]
            {
                "RunCatalogNodes", "RunCatalogPages", "RunCatalogMembers", "RunCatalogSets", "RunCatalogSetNodes",
                "RunCandidates", "RunCandidateOrigins", "RunCandidateDispositions", "RunPatches", "RunPatchDispositions",
            })
            {
                migrationBuilder.Sql(
                    $"CREATE TRIGGER \"TR_{table}_AppendOnly\" BEFORE UPDATE OR DELETE ON \"{table}\" FOR EACH ROW EXECUTE FUNCTION \"bprojects_forbid_work_mutation\"()");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunCandidateDispositions");

            migrationBuilder.DropTable(
                name: "RunCandidateOrigins");

            migrationBuilder.DropTable(
                name: "RunCatalogMembers");

            migrationBuilder.DropTable(
                name: "RunCatalogPages");

            migrationBuilder.DropTable(
                name: "RunCatalogSetNodes");

            migrationBuilder.DropTable(
                name: "RunPatchDispositions");

            migrationBuilder.DropTable(
                name: "RunCandidates");

            migrationBuilder.DropTable(
                name: "RunCatalogNodes");

            migrationBuilder.DropTable(
                name: "RunCatalogSets");

            migrationBuilder.DropTable(
                name: "RunPatches");
        }
    }
}
