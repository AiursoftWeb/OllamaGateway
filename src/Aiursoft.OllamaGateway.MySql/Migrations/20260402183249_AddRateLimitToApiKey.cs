using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.MySql.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitToApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxRequests",
                table: "ApiKeys",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "RateLimitEnabled",
                table: "ApiKeys",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RateLimitHang",
                table: "ApiKeys",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TimeWindowSeconds",
                table: "ApiKeys",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxRequests",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitEnabled",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RateLimitHang",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "TimeWindowSeconds",
                table: "ApiKeys");
        }
    }
}
