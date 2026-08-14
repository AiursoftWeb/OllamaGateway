using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderOpenAiProtocolCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SupportsOpenAiChatCompletions",
                table: "OllamaProviders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SupportsOpenAiResponses",
                table: "OllamaProviders",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportsOpenAiChatCompletions",
                table: "OllamaProviders");

            migrationBuilder.DropColumn(
                name: "SupportsOpenAiResponses",
                table: "OllamaProviders");
        }
    }
}
