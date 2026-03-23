using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MoveWarmupToProviderFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepAliveWarmup",
                table: "VirtualModelBackends");

            migrationBuilder.AddColumn<string>(
                name: "WarmupModelsJson",
                table: "OllamaProviders",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WarmupModelsJson",
                table: "OllamaProviders");

            migrationBuilder.AddColumn<bool>(
                name: "KeepAliveWarmup",
                table: "VirtualModelBackends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
