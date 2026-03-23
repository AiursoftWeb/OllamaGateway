using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.MySql.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMaxContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxContext",
                table: "VirtualModels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxContext",
                table: "VirtualModels",
                type: "int",
                nullable: true);
        }
    }
}
