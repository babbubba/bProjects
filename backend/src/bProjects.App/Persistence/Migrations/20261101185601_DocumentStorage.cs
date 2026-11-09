using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceSnapshotId",
                table: "ProcessingRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceSnapshotId",
                table: "ChatTurns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentPolicies",
                columns: table => new
                {
                    Id = table.Column<short>(type: "smallint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    LimitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPolicies", x => x.Id);
                    table.CheckConstraint("CK_DocumentPolicies_Revision", "\"Revision\" >= 1");
                    table.CheckConstraint("CK_DocumentPolicies_Singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_DocumentPolicies_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSourceSelections",
                columns: table => new
                {
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSourceSelections", x => x.ProjectId);
                    table.CheckConstraint("CK_ProjectSourceSelections_Revision", "\"Revision\" >= 0");
                    table.ForeignKey(
                        name: "FK_ProjectSourceSelections_AspNetUsers_UpdatedBy",
                        column: x => x.UpdatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectSourceSelections_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectionRevision = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_SourceSnapshots_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_SourceSnapshots_Revision", "\"SelectionRevision\" >= 0");
                    table.ForeignKey(
                        name: "FK_SourceSnapshots_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: true),
                    RetryOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    ConfigJson = table.Column<string>(type: "jsonb", nullable: true),
                    WorkflowDefinitionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentJobs", x => x.Id);
                    table.UniqueConstraint("AK_DocumentJobs_Id_PreparationId", x => new { x.Id, x.PreparationId });
                    table.CheckConstraint("CK_DocumentJobs_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                    table.CheckConstraint("CK_DocumentJobs_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultHash\" IS NOT NULL");
                    table.CheckConstraint("CK_DocumentJobs_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_DocumentJobs_AspNetUsers_RequestedBy",
                        column: x => x.RequestedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentJobs_DocumentJobs_RetryOfId",
                        column: x => x.RetryOfId,
                        principalTable: "DocumentJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentPreparations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    ParseJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParserVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurrentEvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RetryOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    PolicyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HardDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EffectiveDeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CallCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPreparations", x => x.Id);
                    table.UniqueConstraint("AK_DocumentPreparations_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_DocumentPreparations_Calls", "\"CallCount\" >= 0");
                    table.CheckConstraint("CK_DocumentPreparations_Deadlines", "\"EffectiveDeadlineAt\" <= \"HardDeadlineAt\" AND \"HardDeadlineAt\" > \"RequestedAt\"");
                    table.CheckConstraint("CK_DocumentPreparations_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.CheckConstraint("CK_DocumentPreparations_Generation", "\"Generation\" >= 1");
                    table.CheckConstraint("CK_DocumentPreparations_Ready_Evidence", "\"Status\" <> 'Ready' OR \"CurrentEvidenceVersionId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_DocumentPreparations_DocumentJobs_ParseJobId_Id",
                        columns: x => new { x.ParseJobId, x.Id },
                        principalTable: "DocumentJobs",
                        principalColumns: new[] { "Id", "PreparationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPreparations_DocumentPreparations_RetryOfId",
                        column: x => x.RetryOfId,
                        principalTable: "DocumentPreparations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPreparations_EvidenceExtractionVersions_CurrentEvid~",
                        column: x => x.CurrentEvidenceVersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MediaType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ByteLength = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UploadReceiptId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentVersions", x => x.Id);
                    table.UniqueConstraint("AK_DocumentVersions_Id_DocumentId", x => new { x.Id, x.DocumentId });
                    table.UniqueConstraint("AK_DocumentVersions_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_DocumentVersions_Sha256", "length(\"Sha256\") = 64");
                    table.CheckConstraint("CK_DocumentVersions_Size", "\"ByteLength\" > 0");
                    table.CheckConstraint("CK_DocumentVersions_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_DocumentVersions_AspNetUsers_UploadedBy",
                        column: x => x.UploadedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LatestReadyVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectDocuments", x => x.Id);
                    table.UniqueConstraint("AK_ProjectDocuments_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_ProjectDocuments_Name_NotBlank", "\"DisplayName\" ~ '\\S'");
                    table.ForeignKey(
                        name: "FK_ProjectDocuments_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectDocuments_DocumentVersions_LatestReadyVersionId_Id",
                        columns: x => new { x.LatestReadyVersionId, x.Id },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectDocuments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSourceSelectionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PinnedVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSourceSelectionItems", x => x.Id);
                    table.CheckConstraint("CK_ProjectSourceSelectionItems_Pinned", "(\"Mode\" = 'Pinned') = (\"PinnedVersionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ProjectSourceSelectionItems_DocumentVersions_PinnedVersionI~",
                        columns: x => new { x.PinnedVersionId, x.DocumentId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectSourceSelectionItems_ProjectDocuments_DocumentId_Pro~",
                        columns: x => new { x.DocumentId, x.ProjectId },
                        principalTable: "ProjectDocuments",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectSourceSelectionItems_ProjectSourceSelections_Project~",
                        column: x => x.ProjectId,
                        principalTable: "ProjectSourceSelections",
                        principalColumn: "ProjectId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SourceSnapshotItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false),
                    Availability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreparationId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceSnapshotItems", x => x.Id);
                    table.CheckConstraint("CK_SourceSnapshotItems_EvidenceOnlyReady", "\"EvidenceVersionId\" IS NULL OR \"Availability\" = 'Ready'");
                    table.CheckConstraint("CK_SourceSnapshotItems_Excluded", "(\"Availability\" = 'Excluded') = (NOT \"Included\")");
                    table.CheckConstraint("CK_SourceSnapshotItems_Ready", "(\"Availability\" = 'Ready') = (\"DocumentVersionId\" IS NOT NULL AND \"PreparationId\" IS NOT NULL AND \"EvidenceVersionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_SourceSnapshotItems_DocumentPreparations_PreparationId_Proj~",
                        columns: x => new { x.PreparationId, x.ProjectId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceSnapshotItems_DocumentVersions_DocumentVersionId_Docu~",
                        columns: x => new { x.DocumentVersionId, x.DocumentId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "DocumentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceSnapshotItems_EvidenceExtractionVersions_EvidenceVers~",
                        column: x => x.EvidenceVersionId,
                        principalTable: "EvidenceExtractionVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceSnapshotItems_ProjectDocuments_DocumentId_ProjectId",
                        columns: x => new { x.DocumentId, x.ProjectId },
                        principalTable: "ProjectDocuments",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SourceSnapshotItems_SourceSnapshots_SnapshotId_ProjectId",
                        columns: x => new { x.SnapshotId, x.ProjectId },
                        principalTable: "SourceSnapshots",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UploadReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestKey = table.Column<Guid>(type: "uuid", nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultVersionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadReceipts", x => x.Id);
                    table.UniqueConstraint("AK_UploadReceipts_Id_ProjectId", x => new { x.Id, x.ProjectId });
                    table.CheckConstraint("CK_UploadReceipts_Reserved", "\"ReservedBytes\" >= 0");
                    table.CheckConstraint("CK_UploadReceipts_Result", "(\"Status\" = 'Committed') = (\"ResultVersionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_UploadReceipts_DocumentVersions_ResultVersionId_ProjectId",
                        columns: x => new { x.ResultVersionId, x.ProjectId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadReceipts_ProjectDocuments_DocumentId_ProjectId",
                        columns: x => new { x.DocumentId, x.ProjectId },
                        principalTable: "ProjectDocuments",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UploadReceipts_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_SourceSnapshotId",
                table: "ProcessingRuns",
                column: "SourceSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingRuns_SourceSnapshotId_ProjectId",
                table: "ProcessingRuns",
                columns: new[] { "SourceSnapshotId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_SourceSnapshotId",
                table: "ChatTurns",
                column: "SourceSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_Open_Fifo",
                table: "DocumentJobs",
                columns: new[] { "RequestedAt", "Id" },
                filter: "\"Status\" IN ('Queued', 'Running')");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_PreparationId_Kind_UnitId",
                table: "DocumentJobs",
                columns: new[] { "PreparationId", "Kind", "UnitId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_PreparationId_ProjectId",
                table: "DocumentJobs",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_ProjectId_RequestKey",
                table: "DocumentJobs",
                columns: new[] { "ProjectId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_RequestedBy",
                table: "DocumentJobs",
                column: "RequestedBy");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentJobs_RetryOfId",
                table: "DocumentJobs",
                column: "RetryOfId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPolicies_UpdatedBy",
                table: "DocumentPolicies",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_CurrentEvidenceVersionId",
                table: "DocumentPreparations",
                column: "CurrentEvidenceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_DocumentVersionId_Generation",
                table: "DocumentPreparations",
                columns: new[] { "DocumentVersionId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_DocumentVersionId_ProjectId",
                table: "DocumentPreparations",
                columns: new[] { "DocumentVersionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_ParseJobId",
                table: "DocumentPreparations",
                column: "ParseJobId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_ParseJobId_Id",
                table: "DocumentPreparations",
                columns: new[] { "ParseJobId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPreparations_RetryOfId",
                table: "DocumentPreparations",
                column: "RetryOfId");

            migrationBuilder.CreateIndex(
                name: "UX_DocumentPreparations_OneOpenPerVersion",
                table: "DocumentPreparations",
                column: "DocumentVersionId",
                unique: true,
                filter: "\"Status\" IN ('Queued', 'Parsing', 'Extracting')");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_DocumentId_ProjectId",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_DocumentId_Version",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_StorageKey",
                table: "DocumentVersions",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_UploadedBy",
                table: "DocumentVersions",
                column: "UploadedBy");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_UploadReceiptId",
                table: "DocumentVersions",
                column: "UploadReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentVersions_UploadReceiptId_ProjectId",
                table: "DocumentVersions",
                columns: new[] { "UploadReceiptId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocuments_CreatedBy",
                table: "ProjectDocuments",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocuments_LatestReadyVersionId_Id",
                table: "ProjectDocuments",
                columns: new[] { "LatestReadyVersionId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectDocuments_ProjectId_CreatedAt",
                table: "ProjectDocuments",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSourceSelectionItems_DocumentId_ProjectId",
                table: "ProjectSourceSelectionItems",
                columns: new[] { "DocumentId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSourceSelectionItems_PinnedVersionId_DocumentId",
                table: "ProjectSourceSelectionItems",
                columns: new[] { "PinnedVersionId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSourceSelectionItems_ProjectId_DocumentId",
                table: "ProjectSourceSelectionItems",
                columns: new[] { "ProjectId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSourceSelections_UpdatedBy",
                table: "ProjectSourceSelections",
                column: "UpdatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_DocumentId_ProjectId",
                table: "SourceSnapshotItems",
                columns: new[] { "DocumentId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_DocumentVersionId_DocumentId",
                table: "SourceSnapshotItems",
                columns: new[] { "DocumentVersionId", "DocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_EvidenceVersionId",
                table: "SourceSnapshotItems",
                column: "EvidenceVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_PreparationId",
                table: "SourceSnapshotItems",
                column: "PreparationId");

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_PreparationId_ProjectId",
                table: "SourceSnapshotItems",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_SnapshotId_DocumentId",
                table: "SourceSnapshotItems",
                columns: new[] { "SnapshotId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshotItems_SnapshotId_ProjectId",
                table: "SourceSnapshotItems",
                columns: new[] { "SnapshotId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_SourceSnapshots_ProjectId_CreatedAt",
                table: "SourceSnapshots",
                columns: new[] { "ProjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadReceipts_DocumentId_ProjectId",
                table: "UploadReceipts",
                columns: new[] { "DocumentId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadReceipts_ProjectId_RequestKey",
                table: "UploadReceipts",
                columns: new[] { "ProjectId", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UploadReceipts_ResultVersionId_ProjectId",
                table: "UploadReceipts",
                columns: new[] { "ResultVersionId", "ProjectId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ChatTurns_SourceSnapshots_SourceSnapshotId",
                table: "ChatTurns",
                column: "SourceSnapshotId",
                principalTable: "SourceSnapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProcessingRuns_SourceSnapshots_SourceSnapshotId_ProjectId",
                table: "ProcessingRuns",
                columns: new[] { "SourceSnapshotId", "ProjectId" },
                principalTable: "SourceSnapshots",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentJobs_DocumentPreparations_PreparationId_ProjectId",
                table: "DocumentJobs",
                columns: new[] { "PreparationId", "ProjectId" },
                principalTable: "DocumentPreparations",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentPreparations_DocumentVersions_DocumentVersionId_Pro~",
                table: "DocumentPreparations",
                columns: new[] { "DocumentVersionId", "ProjectId" },
                principalTable: "DocumentVersions",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentVersions_ProjectDocuments_DocumentId_ProjectId",
                table: "DocumentVersions",
                columns: new[] { "DocumentId", "ProjectId" },
                principalTable: "ProjectDocuments",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentVersions_UploadReceipts_UploadReceiptId_ProjectId",
                table: "DocumentVersions",
                columns: new[] { "UploadReceiptId", "ProjectId" },
                principalTable: "UploadReceipts",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down non distruttiva: dopo la prima scrittura di dati documentali il ritorno al modello 001 perderebbe fonti
            // referenziate. In quel caso la migrazione si rifiuta; il rollback sicuro è il ripristino coerente DB+volume.
            // DocumentPolicies è solo configurazione Admin e non blocca.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""ProjectDocuments"")
       OR EXISTS (SELECT 1 FROM ""DocumentVersions"")
       OR EXISTS (SELECT 1 FROM ""UploadReceipts"")
       OR EXISTS (SELECT 1 FROM ""ProjectSourceSelections"")
       OR EXISTS (SELECT 1 FROM ""DocumentPreparations"")
       OR EXISTS (SELECT 1 FROM ""DocumentJobs"")
       OR EXISTS (SELECT 1 FROM ""SourceSnapshots"")
       OR EXISTS (SELECT 1 FROM ""ChatTurns"" WHERE ""SourceSnapshotId"" IS NOT NULL)
       OR EXISTS (SELECT 1 FROM ""ProcessingRuns"" WHERE ""SourceSnapshotId"" IS NOT NULL) THEN
        RAISE EXCEPTION 'Down rifiutata: esistono dati documentali; ripristinare un backup coerente DB+volume.';
    END IF;
END $$;");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatTurns_SourceSnapshots_SourceSnapshotId",
                table: "ChatTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_ProcessingRuns_SourceSnapshots_SourceSnapshotId_ProjectId",
                table: "ProcessingRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_DocumentJobs_DocumentPreparations_PreparationId_ProjectId",
                table: "DocumentJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectDocuments_DocumentVersions_LatestReadyVersionId_Id",
                table: "ProjectDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_UploadReceipts_DocumentVersions_ResultVersionId_ProjectId",
                table: "UploadReceipts");

            migrationBuilder.DropTable(
                name: "DocumentPolicies");

            migrationBuilder.DropTable(
                name: "ProjectSourceSelectionItems");

            migrationBuilder.DropTable(
                name: "SourceSnapshotItems");

            migrationBuilder.DropTable(
                name: "ProjectSourceSelections");

            migrationBuilder.DropTable(
                name: "SourceSnapshots");

            migrationBuilder.DropTable(
                name: "DocumentPreparations");

            migrationBuilder.DropTable(
                name: "DocumentJobs");

            migrationBuilder.DropTable(
                name: "DocumentVersions");

            migrationBuilder.DropTable(
                name: "UploadReceipts");

            migrationBuilder.DropTable(
                name: "ProjectDocuments");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingRuns_SourceSnapshotId",
                table: "ProcessingRuns");

            migrationBuilder.DropIndex(
                name: "IX_ProcessingRuns_SourceSnapshotId_ProjectId",
                table: "ProcessingRuns");

            migrationBuilder.DropIndex(
                name: "IX_ChatTurns_SourceSnapshotId",
                table: "ChatTurns");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotId",
                table: "ProcessingRuns");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotId",
                table: "ChatTurns");
        }
    }
}
