using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DocumentEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AnalysisEvidenceSelectionItems",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "Evidences",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "Evidences",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "Evidences",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Message");   // backfill: le righe 001 sono tutte di origine messaggio

            migrationBuilder.AddColumn<Guid>(
                name: "PreparationId",
                table: "Evidences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "EvidenceExtractionVersions",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "EvidenceExtractionVersions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "EvidenceExtractionVersions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "ManifestHash",
                table: "EvidenceExtractionVersions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "EvidenceExtractionVersions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Message");   // backfill: le righe 001 sono tutte di origine messaggio

            migrationBuilder.AddColumn<Guid>(
                name: "PreparationId",
                table: "EvidenceExtractionVersions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "EvidenceExtractionJobs",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "EvidenceExtractionJobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "EvidenceExtractionJobs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "EvidenceExtractionJobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Message");   // backfill: le righe 001 sono tutte di origine messaggio

            migrationBuilder.AddColumn<Guid>(
                name: "PreparationId",
                table: "EvidenceExtractionJobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "AnalysisEvidenceSelectionItems",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "AnalysisEvidenceSelectionItems",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "OriginKind",
                table: "AnalysisEvidenceSelectionItems",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Message");   // backfill: le righe 001 sono tutte di origine messaggio

            migrationBuilder.AddColumn<Guid>(
                name: "PreparationId",
                table: "AnalysisEvidenceSelectionItems",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_EvidenceExtractionVersions_Id_ProjectId",
                table: "EvidenceExtractionVersions",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DocumentPreparations_Id_DocumentVersionId",
                table: "DocumentPreparations",
                columns: new[] { "Id", "DocumentVersionId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AnalysisEvidenceSelectionItems",
                table: "AnalysisEvidenceSelectionItems",
                columns: new[] { "RunId", "VersionId" });

            migrationBuilder.CreateTable(
                name: "DocumentPromotions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromPreparationId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromEvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToPreparationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToEvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromotedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentPromotions", x => x.Id);
                    table.CheckConstraint("CK_DocumentPromotions_Changes", "\"FromEvidenceVersionId\" IS NULL OR \"FromEvidenceVersionId\" <> \"ToEvidenceVersionId\"");
                    table.CheckConstraint("CK_DocumentPromotions_From", "(\"FromPreparationId\" IS NULL) = (\"FromEvidenceVersionId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_AspNetUsers_PromotedBy",
                        column: x => x.PromotedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_DocumentPreparations_FromPreparationId_D~",
                        columns: x => new { x.FromPreparationId, x.DocumentVersionId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_DocumentPreparations_ToPreparationId_Doc~",
                        columns: x => new { x.ToPreparationId, x.DocumentVersionId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_DocumentVersions_DocumentVersionId_Proje~",
                        columns: x => new { x.DocumentVersionId, x.ProjectId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_EvidenceExtractionVersions_FromEvidenceV~",
                        columns: x => new { x.FromEvidenceVersionId, x.ProjectId },
                        principalTable: "EvidenceExtractionVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentPromotions_EvidenceExtractionVersions_ToEvidenceVer~",
                        columns: x => new { x.ToEvidenceVersionId, x.ProjectId },
                        principalTable: "EvidenceExtractionVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EvidenceDocumentStates",
                columns: table => new
                {
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentPreparationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CurrentEvidenceVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvidenceDocumentStates", x => x.DocumentVersionId);
                    table.CheckConstraint("CK_EvidenceDocumentStates_Pointers", "(\"CurrentPreparationId\" IS NULL) = (\"CurrentEvidenceVersionId\" IS NULL)");
                    table.CheckConstraint("CK_EvidenceDocumentStates_Revision", "\"Revision\" >= 0");
                    table.ForeignKey(
                        name: "FK_EvidenceDocumentStates_DocumentPreparations_CurrentPreparat~",
                        columns: x => new { x.CurrentPreparationId, x.DocumentVersionId },
                        principalTable: "DocumentPreparations",
                        principalColumns: new[] { "Id", "DocumentVersionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceDocumentStates_DocumentVersions_DocumentVersionId_P~",
                        columns: x => new { x.DocumentVersionId, x.ProjectId },
                        principalTable: "DocumentVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EvidenceDocumentStates_EvidenceExtractionVersions_CurrentEv~",
                        columns: x => new { x.CurrentEvidenceVersionId, x.ProjectId },
                        principalTable: "EvidenceExtractionVersions",
                        principalColumns: new[] { "Id", "ProjectId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_PreparationId_ProjectId",
                table: "Evidences",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidences_ProjectId_PreparationId",
                table: "Evidences",
                columns: new[] { "ProjectId", "PreparationId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Evidences_Origin",
                table: "Evidences",
                sql: "(\"OriginKind\" = 'Message' AND \"MessageId\" IS NOT NULL AND \"PreparationId\" IS NULL) OR (\"OriginKind\" = 'Document' AND \"MessageId\" IS NULL AND \"PreparationId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_PreparationId_ProjectId",
                table: "EvidenceExtractionVersions",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionVersions_PreparationId_Version",
                table: "EvidenceExtractionVersions",
                columns: new[] { "PreparationId", "Version" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_EvidenceExtractionVersions_Origin",
                table: "EvidenceExtractionVersions",
                sql: "(\"OriginKind\" = 'Message' AND \"ConversationId\" IS NOT NULL AND \"MessageId\" IS NOT NULL AND \"MessageSequence\" IS NOT NULL AND \"PreparationId\" IS NULL AND \"ManifestHash\" IS NULL) OR (\"OriginKind\" = 'Document' AND \"ConversationId\" IS NULL AND \"MessageId\" IS NULL AND \"MessageSequence\" IS NULL AND \"PreparationId\" IS NOT NULL AND \"ManifestHash\" IS NOT NULL AND \"SchemaVersion\" = 'evidence-1.1.0')");

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceExtractionJobs_PreparationId_ProjectId",
                table: "EvidenceExtractionJobs",
                columns: new[] { "PreparationId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "UX_EvidenceExtractionJobs_OnePerPreparation",
                table: "EvidenceExtractionJobs",
                column: "PreparationId",
                unique: true,
                filter: "\"OriginKind\" = 'Document'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EvidenceExtractionJobs_Origin",
                table: "EvidenceExtractionJobs",
                sql: "(\"OriginKind\" = 'Message' AND \"ConversationId\" IS NOT NULL AND \"MessageId\" IS NOT NULL AND \"MessageSequence\" IS NOT NULL AND \"PreparationId\" IS NULL) OR (\"OriginKind\" = 'Document' AND \"ConversationId\" IS NULL AND \"MessageId\" IS NULL AND \"MessageSequence\" IS NULL AND \"PreparationId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelectionItems_PreparationId",
                table: "AnalysisEvidenceSelectionItems",
                column: "PreparationId");

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelectionItems_RunId_MessageId",
                table: "AnalysisEvidenceSelectionItems",
                columns: new[] { "RunId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisEvidenceSelectionItems_RunId_PreparationId",
                table: "AnalysisEvidenceSelectionItems",
                columns: new[] { "RunId", "PreparationId" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AnalysisEvidenceSelectionItems_Origin",
                table: "AnalysisEvidenceSelectionItems",
                sql: "(\"OriginKind\" = 'Message' AND \"MessageId\" IS NOT NULL AND \"MessageSequence\" IS NOT NULL AND \"PreparationId\" IS NULL) OR (\"OriginKind\" = 'Document' AND \"MessageId\" IS NULL AND \"MessageSequence\" IS NULL AND \"PreparationId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_DocumentVersionId_ProjectId",
                table: "DocumentPromotions",
                columns: new[] { "DocumentVersionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_DocumentVersionId_PromotedAt",
                table: "DocumentPromotions",
                columns: new[] { "DocumentVersionId", "PromotedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_FromEvidenceVersionId_ProjectId",
                table: "DocumentPromotions",
                columns: new[] { "FromEvidenceVersionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_FromPreparationId_DocumentVersionId",
                table: "DocumentPromotions",
                columns: new[] { "FromPreparationId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_PromotedBy",
                table: "DocumentPromotions",
                column: "PromotedBy");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_ToEvidenceVersionId_ProjectId",
                table: "DocumentPromotions",
                columns: new[] { "ToEvidenceVersionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentPromotions_ToPreparationId_DocumentVersionId",
                table: "DocumentPromotions",
                columns: new[] { "ToPreparationId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceDocumentStates_CurrentEvidenceVersionId_ProjectId",
                table: "EvidenceDocumentStates",
                columns: new[] { "CurrentEvidenceVersionId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceDocumentStates_CurrentPreparationId_DocumentVersion~",
                table: "EvidenceDocumentStates",
                columns: new[] { "CurrentPreparationId", "DocumentVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceDocumentStates_DocumentVersionId_ProjectId",
                table: "EvidenceDocumentStates",
                columns: new[] { "DocumentVersionId", "ProjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EvidenceDocumentStates_ProjectId",
                table: "EvidenceDocumentStates",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_AnalysisEvidenceSelectionItems_DocumentPreparations_Prepara~",
                table: "AnalysisEvidenceSelectionItems",
                column: "PreparationId",
                principalTable: "DocumentPreparations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EvidenceExtractionJobs_DocumentPreparations_PreparationId_P~",
                table: "EvidenceExtractionJobs",
                columns: new[] { "PreparationId", "ProjectId" },
                principalTable: "DocumentPreparations",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EvidenceExtractionVersions_DocumentPreparations_Preparation~",
                table: "EvidenceExtractionVersions",
                columns: new[] { "PreparationId", "ProjectId" },
                principalTable: "DocumentPreparations",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Evidences_DocumentPreparations_PreparationId_ProjectId",
                table: "Evidences",
                columns: new[] { "PreparationId", "ProjectId" },
                principalTable: "DocumentPreparations",
                principalColumns: new[] { "Id", "ProjectId" },
                onDelete: ReferentialAction.Restrict);

            // OriginKind mantiene DEFAULT 'Message' (backfill delle righe 001 e scrittori storici che non lo indicano): non può nascondere
            // un ramo documentale, perché CK_*_Origin richiede PreparationId null per Message. I writer documentali lo dichiarano sempre.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Una volta presenti dati documentali il rollback non è sicuro (MessageId/Preparation tornerebbero obbligatori): restore o release compatibile.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Evidences"" WHERE ""OriginKind"" <> 'Message')
       OR EXISTS (SELECT 1 FROM ""EvidenceExtractionVersions"" WHERE ""OriginKind"" <> 'Message')
       OR EXISTS (SELECT 1 FROM ""EvidenceExtractionJobs"" WHERE ""OriginKind"" <> 'Message')
       OR EXISTS (SELECT 1 FROM ""AnalysisEvidenceSelectionItems"" WHERE ""OriginKind"" <> 'Message')
       OR EXISTS (SELECT 1 FROM ""EvidenceDocumentStates"")
       OR EXISTS (SELECT 1 FROM ""DocumentPromotions"") THEN
        RAISE EXCEPTION 'Down rifiutata: esistono dati Evidence documentali; ripristinare un backup coerente DB+volume.';
    END IF;
END $$;");

            migrationBuilder.DropForeignKey(
                name: "FK_AnalysisEvidenceSelectionItems_DocumentPreparations_Prepara~",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropForeignKey(
                name: "FK_EvidenceExtractionJobs_DocumentPreparations_PreparationId_P~",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_EvidenceExtractionVersions_DocumentPreparations_Preparation~",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropForeignKey(
                name: "FK_Evidences_DocumentPreparations_PreparationId_ProjectId",
                table: "Evidences");

            migrationBuilder.DropTable(
                name: "DocumentPromotions");

            migrationBuilder.DropTable(
                name: "EvidenceDocumentStates");

            migrationBuilder.DropIndex(
                name: "IX_Evidences_PreparationId_ProjectId",
                table: "Evidences");

            migrationBuilder.DropIndex(
                name: "IX_Evidences_ProjectId_PreparationId",
                table: "Evidences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Evidences_Origin",
                table: "Evidences");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_EvidenceExtractionVersions_Id_ProjectId",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropIndex(
                name: "IX_EvidenceExtractionVersions_PreparationId_ProjectId",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropIndex(
                name: "IX_EvidenceExtractionVersions_PreparationId_Version",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EvidenceExtractionVersions_Origin",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropIndex(
                name: "IX_EvidenceExtractionJobs_PreparationId_ProjectId",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropIndex(
                name: "UX_EvidenceExtractionJobs_OnePerPreparation",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EvidenceExtractionJobs_Origin",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DocumentPreparations_Id_DocumentVersionId",
                table: "DocumentPreparations");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AnalysisEvidenceSelectionItems",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisEvidenceSelectionItems_PreparationId",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisEvidenceSelectionItems_RunId_MessageId",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropIndex(
                name: "IX_AnalysisEvidenceSelectionItems_RunId_PreparationId",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AnalysisEvidenceSelectionItems_Origin",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "Evidences");

            migrationBuilder.DropColumn(
                name: "PreparationId",
                table: "Evidences");

            migrationBuilder.DropColumn(
                name: "ManifestHash",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropColumn(
                name: "PreparationId",
                table: "EvidenceExtractionVersions");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropColumn(
                name: "PreparationId",
                table: "EvidenceExtractionJobs");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.DropColumn(
                name: "PreparationId",
                table: "AnalysisEvidenceSelectionItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "Evidences",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Key",
                table: "Evidences",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "EvidenceExtractionVersions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "EvidenceExtractionVersions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "EvidenceExtractionVersions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "EvidenceExtractionJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "EvidenceExtractionJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ConversationId",
                table: "EvidenceExtractionJobs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "MessageSequence",
                table: "AnalysisEvidenceSelectionItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageId",
                table: "AnalysisEvidenceSelectionItems",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_AnalysisEvidenceSelectionItems",
                table: "AnalysisEvidenceSelectionItems",
                columns: new[] { "RunId", "MessageId" });
        }
    }
}
