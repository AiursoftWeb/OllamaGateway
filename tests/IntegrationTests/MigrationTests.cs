using Aiursoft.OllamaGateway.MySql;
using Aiursoft.OllamaGateway.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

/// <summary>
/// This test class ensures that the Entity Framework migrations are up-to-date for all supported database providers.
/// If you change the database model (entities), you must create a new migration for both SQLite and MySQL.
/// </summary>
[TestClass]
public class MigrationTests
{
    [TestMethod]
    public async Task ThinkingMigration_PreservesOllamaValuesAndLeavesOpenAiUnset()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options;
        await using var context = new SqliteContext(options);
        var migrator = context.GetService<IMigrator>();
        var previous = context.Database.GetMigrations().Single(id => id.EndsWith("_AddProviderOpenAiProtocolCapabilities", StringComparison.Ordinal));
        await migrator.MigrateAsync(previous);
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO OllamaProviders (Id, Name, BaseUrl, CreatedAt, KeepAlive, WarmupModelsJson, ProviderType, MaxParallelism, HealthCheckTimeoutSeconds)
            VALUES (1, 'Ollama', 'http://ollama.test', '2026-11-16', '5m', '[]', 0, 0, 60),
                   (2, 'OpenAI', 'http://openai.test', '2026-11-16', '5m', '[]', 1, 0, 60);
            """);
        for (var modelId = 1; modelId <= 3; modelId++)
        {
            bool? thinking = modelId == 1 ? true : modelId == 2 ? false : null;
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO VirtualModels (Id, Name, Type, UseRawOutput, CreatedAt, KeepAlive, HealthCheckTimeout, SelectionStrategy, MaxRetries, UsageCount, Thinking)
                VALUES ({modelId}, {"legacy-" + modelId}, 0, 0, '2026-11-16', 0, 600, 0, 3, 0, {thinking});
                """);
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO VirtualModelBackends (VirtualModelId, ProviderId, UnderlyingModelName, Priority, Weight, Enabled, IsHealthy, IsReady, LastCheckedAt, Protocol)
                VALUES ({modelId}, 1, 'legacy-ollama', 1, 1, 1, 1, 1, '2026-11-16', NULL),
                       ({modelId}, 1, 'explicit-ollama', 1, 1, 1, 1, 1, '2026-11-16', 0),
                       ({modelId}, 1, 'ollama-via-chat', 1, 1, 1, 1, 1, '2026-11-16', 1),
                       ({modelId}, 2, 'legacy-openai', 1, 1, 1, 1, 1, '2026-11-16', NULL),
                       ({modelId}, 2, 'openai-via-ollama', 1, 1, 1, 1, 1, '2026-11-16', 0),
                       ({modelId}, 2, 'responses', 1, 1, 1, 1, 1, '2026-11-16', 2);
                """);
        }
        await migrator.MigrateAsync();
        var backends = await context.VirtualModelBackends.AsNoTracking().ToListAsync();
        Assert.HasCount(18, backends);
        foreach (var backend in backends)
        {
            var native = backend.UnderlyingModelName is "legacy-ollama" or "explicit-ollama" or "openai-via-ollama";
            bool? expected = !native || backend.VirtualModelId == 3 ? null : backend.VirtualModelId == 1;
            Assert.AreEqual(expected, backend.OllamaThinkOverride, backend.UnderlyingModelName);
            Assert.IsNull(backend.ReasoningEffortOverride);
            Assert.IsNull(backend.OpenAiThinkOverride);
        }
        Assert.HasCount(3, await context.VirtualModels.ToListAsync());
    }

    [TestMethod]
    public void TestSqliteMigrations()
    {
        var options = new DbContextOptionsBuilder<SqliteContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        using var context = new SqliteContext(options);
        var hasPendingChanges = context.Database.HasPendingModelChanges();
        Assert.IsFalse(hasPendingChanges, "There are pending model changes for Sqlite. Please run 'dotnet ef migrations add' for Sqlite.");
    }

    [TestMethod]
    [DataRow(8, 0, 31)]
    [DataRow(9, 5, 0)]
    [DataRow(9, 7, 0)]
    public void TestMySqlMigrations(int major, int minor, int patch)
    {
        var options = new DbContextOptionsBuilder<MySqlContext>()
            .UseMySql("Server=localhost;Database=test;Uid=root;Pwd=password;", new MySqlServerVersion(new Version(major, minor, patch)))
            .Options;
        using var context = new MySqlContext(options);
        var hasPendingChanges = context.Database.HasPendingModelChanges();
        Assert.IsFalse(hasPendingChanges, "There are pending model changes for MySql. Please run 'dotnet ef migrations add' for MySql.");
    }
}
