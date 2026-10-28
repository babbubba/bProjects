using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace bProjects.App.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatTurnDispatchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatTurns_ConversationId",
                table: "ChatTurns");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_OneRunningPerConversation",
                table: "ChatTurns",
                column: "ConversationId",
                unique: true,
                filter: "\"Status\" = 'Running'");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_Open_Fifo",
                table: "ChatTurns",
                columns: new[] { "ConversationId", "SourceCutoffSequence" },
                filter: "\"Status\" IN ('Queued', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatTurns_OneRunningPerConversation",
                table: "ChatTurns");

            migrationBuilder.DropIndex(
                name: "IX_ChatTurns_Open_Fifo",
                table: "ChatTurns");

            migrationBuilder.CreateIndex(
                name: "IX_ChatTurns_ConversationId",
                table: "ChatTurns",
                column: "ConversationId");
        }
    }
}
