using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentParsing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentPortions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    RangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContextRangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NoEvidenceReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPortions", x => x.Id);
                    table.CheckConstraint("CK_DocumentPortions_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultHash\" IS NOT NULL");
                    table.CheckConstraint("CK_DocumentPortions_Ordinal", "\"Ordinal\" >= 0");
                    table.ForeignKey(
                        name: "FK_DocumentPortions_DocumentJobs_JobId_PreparationId",
                        columns: x => new { x.JobId, x.PreparationId },
                        principalTable: "DocumentJobs",
                        principalColumns: new[] { "Id", "PreparationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPortions_DocumentPreparations_PreparationId_Project~",
                        columns: x => new { x.PreparationId, x.ProjectId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ParsedArtifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    ParserConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BlockCount = table.Column<int>(type: "integer", nullable: false),
                    IssueCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParsedArtifacts", x => x.Id);
                    table.UniqueConstraint("AK_ParsedArtifacts_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_ParsedArtifacts_Counts", "\"BlockCount\" >= 0 AND \"IssueCount\" >= 0 AND \"FencingToken\" >= 1");
                    table.CheckConstraint("CK_ParsedArtifacts_Size", "\"ByteLength\" > 0");
                    table.ForeignKey(
                        name: "FK_ParsedArtifacts_DocumentJobs_JobId_PreparationId",
                        columns: x => new { x.JobId, x.PreparationId },
                        principalTable: "DocumentJobs",
                        principalColumns: new[] { "Id", "PreparationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParsedArtifacts_DocumentPreparations_PreparationId_ProjectId",
                        columns: x => new { x.PreparationId, x.ProjectId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentBlocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CanonicalText = table.Column<string>(type: "text", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HeadingPathJson = table.Column<string>(type: "jsonb", nullable: false),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    BboxJson = table.Column<string>(type: "jsonb", nullable: true),
                    CoordinateSystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DoclingRef = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LineStart = table.Column<int>(type: "integer", nullable: true),
                    LineEnd = table.Column<int>(type: "integer", nullable: true),
                    TableCellsJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentBlocks", x => x.Id);
                    table.UniqueConstraint("AK_DocumentBlocks_Id_ArtifactId", x => new { x.Id, x.ArtifactId });
                    table.UniqueConstraint("AK_DocumentBlocks_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_DocumentBlocks_Lines", "(\"LineStart\" IS NULL) = (\"LineEnd\" IS NULL) AND (\"LineStart\" IS NULL OR (\"LineStart\" >= 1 AND \"LineEnd\" >= \"LineStart\"))");
                    table.CheckConstraint("CK_DocumentBlocks_Ordinal", "\"Ordinal\" >= 0");
                    table.CheckConstraint("CK_DocumentBlocks_Page", "\"Page\" IS NULL OR \"Page\" >= 1");
                    table.CheckConstraint("CK_DocumentBlocks_Parent", "\"ParentId\" IS NULL OR \"ParentId\" <> \"Id\"");
                    table.ForeignKey(
                        name: "FK_DocumentBlocks_DocumentBlocks_ParentId_ArtifactId",
                        columns: x => new { x.ParentId, x.ArtifactId },
                        principalTable: "DocumentBlocks",
                        principalColumns: new[] { "Id", "ArtifactId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentBlocks_ParsedArtifacts_ArtifactId_ProjectId",
                        columns: x => new { x.ArtifactId, x.ProjectId },
                        principalTable: "ParsedArtifacts",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PreparationIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    Page = table.Column<int>(type: "integer", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AffectsCompleteness = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreparationIssues", x => x.Id);
                    table.CheckConstraint("CK_PreparationIssues_Page", "\"Page\" IS NULL OR \"Page\" >= 1");
                    table.ForeignKey(
                        name: "FK_PreparationIssues_DocumentBlocks_BlockId_ProjectId",
                        columns: x => new { x.BlockId, x.ProjectId },
                        principalTable: "DocumentBlocks",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreparationIssues_DocumentPreparations_PreparationId_Projec~",
                        columns: x => new { x.PreparationId, x.ProjectId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBlocks_ArtifactId_Ordinal",
                table: "DocumentBlocks",
                columns: new[] { "ArtifactId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBlocks_ArtifactId_ProjectId",
                table: "DocumentBlocks",
                columns: new[] { "ArtifactId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBlocks_ArtifactId_Ref",
                table: "DocumentBlocks",
                columns: new[] { "ArtifactId", "Ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentBlocks_ParentId_ArtifactId",
                table: "DocumentBlocks",
                columns: new[] { "ParentId", "ArtifactId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPortions_JobId",
                table: "DocumentPortions",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPortions_JobId_PreparationId",
                table: "DocumentPortions",
                columns: new[] { "JobId", "PreparationId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPortions_PreparationId_Ordinal",
                table: "DocumentPortions",
                columns: new[] { "PreparationId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPortions_PreparationId_ProjectId",
                table: "DocumentPortions",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParsedArtifacts_JobId_PreparationId",
                table: "ParsedArtifacts",
                columns: new[] { "JobId", "PreparationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParsedArtifacts_PreparationId",
                table: "ParsedArtifacts",
                column: "PreparationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ParsedArtifacts_PreparationId_ProjectId",
                table: "ParsedArtifacts",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParsedArtifacts_StorageKey",
                table: "ParsedArtifacts",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreparationIssues_BlockId",
                table: "PreparationIssues",
                column: "BlockId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationIssues_BlockId_ProjectId",
                table: "PreparationIssues",
                columns: new[] { "BlockId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_PreparationIssues_PreparationId",
                table: "PreparationIssues",
                column: "PreparationId");

            migrationBuilder.CreateIndex(
                name: "IX_PreparationIssues_PreparationId_ProjectId",
                table: "PreparationIssues",
                columns: new[] { "PreparationId", "ProjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down non distruttiva: artefatti, blocchi, porzioni e segnalazioni sono derivati referenziati dalle fonti; con dati presenti
            // il ritorno li perderebbe. Il rollback sicuro è il ripristino coerente DB+volume.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""ParsedArtifacts"")
       OR EXISTS (SELECT 1 FROM ""DocumentBlocks"")
       OR EXISTS (SELECT 1 FROM ""DocumentPortions"")
       OR EXISTS (SELECT 1 FROM ""PreparationIssues"") THEN
        RAISE EXCEPTION 'Down rifiutata: esistono artefatti di parsing; ripristinare un backup coerente DB+volume.';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "DocumentPortions");

            migrationBuilder.DropTable(
                name: "PreparationIssues");

            migrationBuilder.DropTable(
                name: "DocumentBlocks");

            migrationBuilder.DropTable(
                name: "ParsedArtifacts");
        }
    }
}
