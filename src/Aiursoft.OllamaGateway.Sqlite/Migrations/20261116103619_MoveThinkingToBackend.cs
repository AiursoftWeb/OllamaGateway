using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.OllamaGateway.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class MoveThinkingToBackend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OllamaThinkOverride",
                table: "VirtualModelBackends",
                type: "INTEGER",
                nullable: true);

            // Protocol overrides provider type; a null protocol retains legacy provider inference.
            migrationBuilder.Sql("""
                UPDATE VirtualModelBackends
                SET OllamaThinkOverride = (
                    SELECT Thinking FROM VirtualModels WHERE VirtualModels.Id = VirtualModelBackends.VirtualModelId
                )
                WHERE Protocol = 0 OR (Protocol IS NULL AND ProviderId IN (
                    SELECT Id FROM OllamaProviders WHERE ProviderType = 0
                ));
                """);

            migrationBuilder.DropColumn(name: "Thinking", table: "VirtualModels");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Thinking",
                table: "VirtualModels",
                type: "INTEGER",
                nullable: true);

            // A model-wide setting cannot represent independent backend values.
            // Restore it only when every backend has the same explicit value.
            migrationBuilder.Sql("""
                UPDATE VirtualModels SET Thinking = (
                    SELECT MIN(OllamaThinkOverride) FROM VirtualModelBackends
                    WHERE VirtualModelId = VirtualModels.Id
                    HAVING COUNT(*) = COUNT(OllamaThinkOverride)
                       AND MIN(OllamaThinkOverride) = MAX(OllamaThinkOverride)
                );
                """);

            migrationBuilder.DropColumn(name: "OllamaThinkOverride", table: "VirtualModelBackends");
        }
    }
}
