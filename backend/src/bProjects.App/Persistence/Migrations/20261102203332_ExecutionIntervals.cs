using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExecutionIntervals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExecutionIntervals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExecutionIntervals", x => x.Id);
                    table.CheckConstraint("CK_ExecutionIntervals_Kinds", "\"Kind\" IN ('Queue', 'Preflight', 'Backoff', 'Recovery') AND \"RootKind\" IN ('ProcessingRun', 'ChatTurn', 'DocumentPreparation')");
                    table.CheckConstraint("CK_ExecutionIntervals_Order", "\"EndedAt\" >= \"StartedAt\" AND \"Attempt\" >= 1");
                    table.ForeignKey(
                        name: "FK_ExecutionIntervals_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionIntervals_ProjectId",
                table: "ExecutionIntervals",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "UX_ExecutionIntervals_Root",
                table: "ExecutionIntervals",
                columns: new[] { "RootKind", "RootId", "Kind", "Attempt" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExecutionIntervals");
        }
    }
}
