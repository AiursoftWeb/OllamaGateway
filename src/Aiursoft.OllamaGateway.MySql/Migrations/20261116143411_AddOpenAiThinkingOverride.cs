using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.MySql.Migrations
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
                type: "tinyint(1)",
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
