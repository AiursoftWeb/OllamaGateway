using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.MySql.Migrations
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
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
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
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }
    }
}
