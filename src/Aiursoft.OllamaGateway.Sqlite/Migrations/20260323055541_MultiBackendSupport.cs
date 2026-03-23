using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
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
                type: "TEXT",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<int>(
                name: "ProviderId",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "HealthCheckTimeout",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxContext",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRetries",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SelectionStrategy",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "VirtualModelBackends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VirtualModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    UnderlyingModelName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsHealthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateIndex(
                name: "IX_VirtualModelBackends_ProviderId",
                table: "VirtualModelBackends",
                column: "ProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_VirtualModelBackends_VirtualModelId",
                table: "VirtualModelBackends",
                column: "VirtualModelId");

            migrationBuilder.Sql("INSERT INTO VirtualModelBackends (VirtualModelId, ProviderId, UnderlyingModelName, Priority, Weight, Enabled, IsHealthy, LastCheckedAt) SELECT Id, ProviderId, UnderlyingModel, 0, 1, 1, 1, CURRENT_TIMESTAMP FROM VirtualModels WHERE ProviderId IS NOT NULL AND UnderlyingModel IS NOT NULL;");

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

            migrationBuilder.AlterColumn<string>(
                name: "UnderlyingModel",
                table: "VirtualModels",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ProviderId",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
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
