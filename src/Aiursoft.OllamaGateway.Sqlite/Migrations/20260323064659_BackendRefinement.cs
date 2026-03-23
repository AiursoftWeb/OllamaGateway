using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class BackendRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsReady",
                table: "VirtualModelBackends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "KeepAlive",
                table: "VirtualModelBackends",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeepAlive",
                table: "OllamaProviders",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsReady",
                table: "VirtualModelBackends");

            migrationBuilder.DropColumn(
                name: "KeepAlive",
                table: "VirtualModelBackends");

            migrationBuilder.DropColumn(
                name: "KeepAlive",
                table: "OllamaProviders");
        }
    }
}
