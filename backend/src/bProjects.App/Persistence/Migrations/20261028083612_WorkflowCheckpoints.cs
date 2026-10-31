using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProcessingRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkflowSessionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CheckpointKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CheckpointInfo = table.Column<string>(type: "jsonb", nullable: false),
                    ParentCheckpointKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ParentInfo = table.Column<string>(type: "jsonb", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CommitSequence = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MafVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkflowDefinitionVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowCheckpoints", x => x.Id);
                    table.UniqueConstraint("AK_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Check~", x => new { x.ProcessingRunId, x.WorkflowSessionId, x.CheckpointKey });
                    table.CheckConstraint("CK_WorkflowCheckpoints_Sequence", "\"CommitSequence\" >= 1");
                    table.ForeignKey(
                        name: "FK_WorkflowCheckpoints_ProcessingRuns_ProcessingRunId",
                        column: x => x.ProcessingRunId,
                        principalTable: "ProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowCheckpoints_WorkflowCheckpoints_ProcessingRunId_Wor~",
                        columns: x => new { x.ProcessingRunId, x.WorkflowSessionId, x.ParentCheckpointKey },
                        principalTable: "WorkflowCheckpoints",
                        principalColumns: new[] { "ProcessingRunId", "WorkflowSessionId", "CheckpointKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_OldestFirst",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "CommitSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Check~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "WorkflowSessionId", "CheckpointKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_ProcessingRunId_WorkflowSessionId_Paren~",
                table: "WorkflowCheckpoints",
                columns: new[] { "ProcessingRunId", "WorkflowSessionId", "ParentCheckpointKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowCheckpoints");
        }
    }
}
