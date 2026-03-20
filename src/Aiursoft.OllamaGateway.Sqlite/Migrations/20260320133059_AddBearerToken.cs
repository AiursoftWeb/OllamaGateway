using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddBearerToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BearerToken",
                table: "OllamaProviders",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BearerToken",
                table: "OllamaProviders");
        }
    }
}
