using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InferenceCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InferenceCalls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChatTurnId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessingRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    Phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ContextWindowTokens = table.Column<int>(type: "integer", nullable: true),
                    MaxOutputTokensRequested = table.Column<int>(type: "integer", nullable: true),
                    ServerDefaultNPredict = table.Column<int>(type: "integer", nullable: true),
                    ReasoningRequested = table.Column<bool>(type: "boolean", nullable: false),
                    ReasoningEffective = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: true),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: true),
                    TotalTokens = table.Column<int>(type: "integer", nullable: true),
                    CachedTokens = table.Column<int>(type: "integer", nullable: true),
                    FinishReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsFinal = table.Column<bool>(type: "boolean", nullable: false),
                    Composition = table.Column<string>(type: "jsonb", nullable: true),
                    History = table.Column<string>(type: "jsonb", nullable: true),
                    Compaction = table.Column<string>(type: "jsonb", nullable: true),
                    Config = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InferenceCalls", x => x.Id);
                    table.CheckConstraint("CK_InferenceCalls_Final", "NOT \"IsFinal\" OR (\"State\" = 'Completed' AND \"Phase\" <> 'Compaction')");
                    table.CheckConstraint("CK_InferenceCalls_Owner", "(\"ChatTurnId\" IS NULL) <> (\"ProcessingRunId\" IS NULL)");
                    table.CheckConstraint("CK_InferenceCalls_Tokens", "(\"PromptTokens\" IS NULL OR \"PromptTokens\" >= 0) AND (\"CompletionTokens\" IS NULL OR \"CompletionTokens\" >= 0) AND (\"TotalTokens\" IS NULL OR \"TotalTokens\" >= 0) AND (\"CachedTokens\" IS NULL OR \"CachedTokens\" >= 0)");
                    table.ForeignKey(
                        name: "FK_InferenceCalls_ChatTurns_ChatTurnId",
                        column: x => x.ChatTurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InferenceCalls_ProcessingRuns_ProcessingRunId",
                        column: x => x.ProcessingRunId,
                        principalTable: "ProcessingRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InferenceCalls_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_ChatTurnId_Attempt",
                table: "InferenceCalls",
                columns: new[] { "ChatTurnId", "Attempt" });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_ProcessingRunId_Attempt",
                table: "InferenceCalls",
                columns: new[] { "ProcessingRunId", "Attempt" });

            migrationBuilder.CreateIndex(
                name: "IX_InferenceCalls_Project_LastFinal",
                table: "InferenceCalls",
                columns: new[] { "ProjectId", "StartedAt" },
                descending: new[] { false, true },
                filter: "\"IsFinal\"");

            migrationBuilder.CreateIndex(
                name: "UX_InferenceCalls_OneFinalPerRun",
                table: "InferenceCalls",
                column: "ProcessingRunId",
                unique: true,
                filter: "\"IsFinal\"");

            migrationBuilder.CreateIndex(
                name: "UX_InferenceCalls_OneFinalPerTurn",
                table: "InferenceCalls",
                column: "ChatTurnId",
                unique: true,
                filter: "\"IsFinal\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InferenceCalls");
        }
    }
}
