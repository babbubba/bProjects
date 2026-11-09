using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetrievalIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 003 T006 (data-model.md): indice DERIVATO e ricostruibile dai soli DocumentBlock; nessuna tabella autorevole, nessuna modifica alle tabelle esistenti.
            // L'estensione si crea solo con `migrate` esplicito (mai all'avvio del server) e richiede un'immagine PostgreSQL che includa pgvector (ADR-0002).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector");

            migrationBuilder.CreateTable(
                name: "RetrievalIndexEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndexConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Tsv = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetrievalIndexEntries", x => x.Id);
                    table.CheckConstraint("CK_RetrievalIndexEntries_ConfigHash", "\"IndexConfigHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RetrievalIndexEntries_TextHash", "length(\"TextHash\") > 0");
                });


            // Vincoli e campi da data-model.md, verbatim: Embedding vector(1024) NOT NULL (L2-normalizzato, dimensione = Retrieval:Embedding:Dimension; coseno con `<=>`);
            // BlockId unico insieme a IndexConfigHash; Tsv = to_tsvector('italian', testo indicizzato) con indice GIN; ProjectId/DocumentId/DocumentVersionId denormalizzati
            // dal blocco per filtrare PRIMA del ranking; RetrievalIndexState Ready solo con EntryCount = BlockCount. Indice HNSW non creato: solo se una misura lo giustifica (T019).
            // La colonna non è mappata in EF (nessun pacchetto aggiuntivo): scritta e interrogata solo con SQL esplicito.
            migrationBuilder.Sql("ALTER TABLE \"RetrievalIndexEntries\" ADD COLUMN \"Embedding\" vector(1024) NOT NULL");

            migrationBuilder.CreateTable(
                name: "RetrievalIndexStates",
                columns: table => new
                {
                    DocumentVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndexConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlockCount = table.Column<int>(type: "integer", nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BuiltAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetrievalIndexStates", x => new { x.DocumentVersionId, x.IndexConfigHash });
                    table.CheckConstraint("CK_RetrievalIndexStates_ConfigHash", "\"IndexConfigHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_RetrievalIndexStates_Counts", "\"BlockCount\" >= 0 AND \"EntryCount\" >= 0");
                    table.CheckConstraint("CK_RetrievalIndexStates_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.CheckConstraint("CK_RetrievalIndexStates_Ready", "\"Status\" <> 'Ready' OR (\"EntryCount\" = \"BlockCount\" AND \"BuiltAt\" IS NOT NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalIndexEntries_BlockId_IndexConfigHash",
                table: "RetrievalIndexEntries",
                columns: new[] { "BlockId", "IndexConfigHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalIndexEntries_DocumentVersionId_IndexConfigHash",
                table: "RetrievalIndexEntries",
                columns: new[] { "DocumentVersionId", "IndexConfigHash" });

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalIndexEntries_ProjectId_IndexConfigHash",
                table: "RetrievalIndexEntries",
                columns: new[] { "ProjectId", "IndexConfigHash" });

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalIndexEntries_Tsv",
                table: "RetrievalIndexEntries",
                column: "Tsv")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_RetrievalIndexStates_ProjectId_IndexConfigHash",
                table: "RetrievalIndexStates",
                columns: new[] { "ProjectId", "IndexConfigHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo l'indice derivato: blocchi, evidenze e snapshot non si toccano. L'estensione `vector` resta installata (altri oggetti potrebbero usarla).
            migrationBuilder.DropTable(
                name: "RetrievalIndexEntries");

            migrationBuilder.DropTable(
                name: "RetrievalIndexStates");
        }
    }
}
