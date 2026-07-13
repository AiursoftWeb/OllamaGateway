using System.Reflection;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Aiursoft.OllamaGateway.Models.SystemViewModels;
using Aiursoft.WebTools.Attributes;

namespace Aiursoft.OllamaGateway.Controllers;

/// <summary>
/// This controller is used to handle system related actions like shutdown.
/// </summary>
[Authorize]
[LimitPerMin]
public class SystemController(
    ILogger<SystemController> logger,
    TemplateDbContext dbContext,
    IServiceScopeFactory scopeFactory) : Controller
{
    private static readonly MethodInfo LongCountAsyncMethod = typeof(EntityFrameworkQueryableExtensions)
        .GetMethods(BindingFlags.Static | BindingFlags.Public)
        .Single(method => method.Name == nameof(EntityFrameworkQueryableExtensions.LongCountAsync)
                          && method.GetParameters().Length == 2);
    private const int TableCountConcurrency = 4;

    [Authorize(Policy = AppPermissionNames.CanViewSystemContext)]
    [RenderInNavBar(
        NavGroupName = "Administration",
        NavGroupOrder = 9999,
        CascadedLinksGroupName = "System",
        CascadedLinksIcon = "settings",
        CascadedLinksOrder = 9999,
        LinkText = "Info",
        LinkOrder = 1)]
    public async Task<IActionResult> Index()
    {
        var cancellationToken = HttpContext.RequestAborted;
        var tableCounts = await GetTableCountsAsync(cancellationToken);
        var (applied, defined, pending) = await GetMigrationInfoAsync(cancellationToken);

        return this.StackView(new IndexViewModel
        {
            TableCounts = tableCounts,
            AppliedMigrations = applied,
            TotalDefinedMigrations = defined,
            PendingMigrations = pending,
        });
    }

    [HttpPost]
    [Authorize(Policy = AppPermissionNames.CanRebootThisApp)] // Use the specific permission
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Shutdown([FromServices] IHostApplicationLifetime appLifetime)
    {
        logger.LogWarning("Application shutdown was requested by user: '{UserName}'", User.Identity?.Name);
        appLifetime.StopApplication();
        return Accepted();
    }

    private async Task<List<TableCountEntry>> GetTableCountsAsync(CancellationToken cancellationToken)
    {
        var tables = GetDbSetTables();
        using var limiter = new SemaphoreSlim(TableCountConcurrency);
        var countTasks = tables.Select(table => CountTableRowsWithLimitAsync(table, limiter, cancellationToken));
        var tableCounts = await Task.WhenAll(countTasks);
        return tableCounts
            .OfType<TableCountEntry>()
            .OrderBy(table => table.Name)
            .ToList();
    }

    private List<DbSetTable> GetDbSetTables()
    {
        var tables = new List<DbSetTable>();
        var visitedNames = new HashSet<string>();

        for (var type = dbContext.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!visitedNames.Add(property.Name)) continue;
                if (!property.PropertyType.IsGenericType) continue;
                if (property.PropertyType.GetGenericTypeDefinition() != typeof(DbSet<>)) continue;

                var entityType = property.PropertyType.GetGenericArguments()[0];
                if (dbContext.Model.FindEntityType(entityType) == null) continue;

                tables.Add(new DbSetTable(property.Name, property, entityType));
            }
        }

        return tables;
    }

    private async Task<TableCountEntry?> CountTableRowsWithLimitAsync(
        DbSetTable table,
        SemaphoreSlim limiter,
        CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken);
        try
        {
            return await CountTableRowsAsync(table, cancellationToken);
        }
        finally
        {
            limiter.Release();
        }
    }

    private async Task<TableCountEntry?> CountTableRowsAsync(DbSetTable table, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scopedDbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var dbSet = table.Property.GetValue(scopedDbContext);
            if (dbSet == null) return null;

            var countTask = (Task<long>?)LongCountAsyncMethod
                .MakeGenericMethod(table.EntityType)
                .Invoke(null, [dbSet, cancellationToken]);
            if (countTask == null) return null;

            return new TableCountEntry
            {
                Name = table.Name,
                Rows = await countTask,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to count rows for DbSet property '{PropertyName}'", table.Name);
            return null;
        }
    }

    private async Task<(List<MigrationEntry> applied, int defined, List<string> pending)> GetMigrationInfoAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var appliedMigrations = (await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken))
                .Select(id => new MigrationEntry { Id = id })
                .ToList();
            var definedMigrations = dbContext.Database.GetMigrations().Count();
            var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();

            return (appliedMigrations, definedMigrations, pendingMigrations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to retrieve migration information");
            return ([], 0, []);
        }
    }

    private sealed record DbSetTable(string Name, PropertyInfo Property, Type EntityType);
}
