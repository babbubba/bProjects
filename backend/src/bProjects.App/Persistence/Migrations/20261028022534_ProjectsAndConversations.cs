using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectsAndConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentAnalysisId = table.Column<Guid>(type: "uuid", nullable: true),
                    LatestDraftId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.CheckConstraint("CK_Projects_Name_NotBlank", "\"Name\" ~ '\\S'");
                    table.ForeignKey(
                        name: "FK_Projects_AspNetUsers_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    MafChatSessionState = table.Column<string>(type: "jsonb", nullable: true),
                    MafVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ChatConfigVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ContextThroughSequence = table.Column<long>(type: "bigint", nullable: false),
                    ChatSessionRevision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChatTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceCutoffSequence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadlineAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResultMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    PromptVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AgentConfig = table.Column<string>(type: "jsonb", nullable: true),
                    SessionRevisionAtStart = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatTurns", x => x.Id);
                    table.CheckConstraint("CK_ChatTurns_Attempts", "\"AttemptCount\" >= 0 AND \"MaxAttempts\" >= 1");
                    table.CheckConstraint("CK_ChatTurns_Completed_Result", "\"Status\" <> 'Completed' OR \"ResultMessageId\" IS NOT NULL");
                    table.CheckConstraint("CK_ChatTurns_Failed_Error", "\"Status\" <> 'Failed' OR \"ErrorCode\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ChatTurns_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReplyToMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChatTurnId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.CheckConstraint("CK_ConversationMessages_Body_NotBlank", "\"Body\" ~ '\\S'");
                    table.CheckConstraint("CK_ConversationMessages_Role_Fields", "(\"Role\" = 'User' AND \"ClientMessageId\" IS NOT NULL AND \"AuthorId\" IS NOT NULL AND \"AgentId\" IS NULL AND \"ReplyToMessageId\" IS NULL AND \"ChatTurnId\" IS NULL) OR (\"Role\" = 'Assistant' AND \"ClientMessageId\" IS NULL AND \"AuthorId\" IS NULL AND \"AgentId\" IS NOT NULL AND \"ReplyToMessageId\" IS NOT NULL AND \"ChatTurnId\" IS NOT NULL)");
                    table.CheckConstraint("CK_ConversationMessages_Sequence", "\"Sequence\" >= 1");
                    table.ForeignKey(
                        name: "FK_ConversationMessages_AspNetUsers_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_ChatTurns_ChatTurnId",
                        column: x => x.ChatTurnId,
                        principalTable: "ChatTurns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_ConversationMessages_ReplyToMessageId",
                        column: x => x.ReplyToMessageId,
                        principalTable: "ConversationMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_ConversationId",
                table: "ChatTurns",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_ResultMessageId",
                table: "ChatTurns",
                column: "ResultMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_TriggerMessageId",
                table: "ChatTurns",
                column: "TriggerMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_AuthorId",
                table: "ConversationMessages",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ChatTurnId",
                table: "ConversationMessages",
                column: "ChatTurnId");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId_ClientMessageId",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId_Sequence",
                table: "ConversationMessages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ReplyToMessageId",
                table: "ConversationMessages",
                column: "ReplyToMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ProjectId",
                table: "Conversations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedAt_Id",
                table: "Projects",
                columns: new[] { "CreatedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_CreatedBy",
                table: "Projects",
                column: "CreatedBy");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatTurns_ConversationMessages_ResultMessageId",
                table: "ChatTurns",
                column: "ResultMessageId",
                principalTable: "ConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ChatTurns_ConversationMessages_TriggerMessageId",
                table: "ChatTurns",
                column: "TriggerMessageId",
                principalTable: "ConversationMessages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Gli originali del dialogo sono append-only: il database rifiuta modifiche e cancellazioni.
            migrationBuilder.Sql("""
                CREATE FUNCTION "ConversationMessages_AppendOnly"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ConversationMessages è append-only' USING ERRCODE = 'restrict_violation';
                END $$;
                CREATE TRIGGER "ConversationMessages_AppendOnly" BEFORE UPDATE OR DELETE ON "ConversationMessages"
                    FOR EACH ROW EXECUTE FUNCTION "ConversationMessages_AppendOnly"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"ConversationMessages_AppendOnly\" ON \"ConversationMessages\"; DROP FUNCTION IF EXISTS \"ConversationMessages_AppendOnly\"();");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatTurns_ConversationMessages_ResultMessageId",
                table: "ChatTurns");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatTurns_ConversationMessages_TriggerMessageId",
                table: "ChatTurns");

            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "ChatTurns");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
