using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenAiThinkingOverride : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OpenAiThinkOverride",
                table: "VirtualModelBackends",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenAiThinkOverride",
                table: "VirtualModelBackends");
        }
    }
}
