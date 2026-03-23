using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.MySql.Migrations
{
    /// <inheritdoc />
    public partial class MultiBackendSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VirtualModels_OllamaProviders_ProviderId",
                table: "VirtualModels");

            migrationBuilder.AlterColumn<string>(
                name: "UnderlyingModel",
                table: "VirtualModels",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldMaxLength: 100)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<int>(
                name: "ProviderId",
                table: "VirtualModels",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckTimeout",
                table: "VirtualModels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxContext",
                table: "VirtualModels",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "VirtualModels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SelectionStrategy",
                table: "VirtualModels",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "VirtualModelBackends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    VirtualModelId = table.Column<int>(type: "int", nullable: false),
                    ProviderId = table.Column<int>(type: "int", nullable: false),
                    UnderlyingModelName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Weight = table.Column<int>(type: "int", nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsHealthy = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VirtualModelBackends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VirtualModelBackends_OllamaProviders_ProviderId",
                        column: x => x.ProviderId,
                        principalTable: "OllamaProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VirtualModelBackends_VirtualModels_VirtualModelId",
                        column: x => x.VirtualModelId,
                        principalTable: "VirtualModels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_VirtualModelBackends_ProviderId",
                table: "VirtualModelBackends",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_VirtualModelBackends_VirtualModelId",
                table: "VirtualModelBackends",
                column: "VirtualModelId");

            migrationBuilder.Sql("INSERT INTO VirtualModelBackends (VirtualModelId, ProviderId, UnderlyingModelName, Priority, Weight, Enabled, IsHealthy, LastCheckedAt) SELECT Id, ProviderId, UnderlyingModel, 0, 1, 1, 1, NOW() FROM VirtualModels WHERE ProviderId IS NOT NULL AND UnderlyingModel IS NOT NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_VirtualModels_OllamaProviders_ProviderId",
                table: "VirtualModels",
                column: "ProviderId",
                principalTable: "OllamaProviders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VirtualModels_OllamaProviders_ProviderId",
                table: "VirtualModels");

            migrationBuilder.DropTable(
                name: "VirtualModelBackends");

            migrationBuilder.DropColumn(
                name: "HealthCheckTimeout",
                table: "VirtualModels");

            migrationBuilder.DropColumn(
                name: "MaxContext",
                table: "VirtualModels");

            migrationBuilder.DropColumn(
                name: "MaxRetries",
                table: "VirtualModels");

            migrationBuilder.DropColumn(
                name: "SelectionStrategy",
                table: "VirtualModels");

            migrationBuilder.UpdateData(
                table: "VirtualModels",
                keyColumn: "UnderlyingModel",
                keyValue: null,
                column: "UnderlyingModel",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "UnderlyingModel",
                table: "VirtualModels",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(100)",
                oldMaxLength: 100,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<int>(
                name: "ProviderId",
                table: "VirtualModels",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_VirtualModels_OllamaProviders_ProviderId",
                table: "VirtualModels",
                column: "ProviderId",
                principalTable: "OllamaProviders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
