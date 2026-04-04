using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.DashboardViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanViewSystemContext)]
[LimitPerMin]
public class DashboardController(
    TemplateDbContext dbContext,
    MemoryUsageTracker memoryUsageTracker,
    ActiveRequestTracker activeRequestTracker) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Dashboard",
        NavGroupOrder = 1,
        CascadedLinksGroupName = "Monitor",
        CascadedLinksIcon = "monitor",
        CascadedLinksOrder = 1,
        LinkText = "Admin Center",
        LinkOrder = 1)]
    public async Task<IActionResult> Index()
    {
        var activeThreshold = DateTime.UtcNow.AddDays(-30);
        var model = new IndexViewModel
        {
            TotalUsers = await dbContext.Users.CountAsync(),
            ActiveUsers = await dbContext.Users.CountAsync(u => u.CreationTime >= activeThreshold),
            TotalApiKeys = await dbContext.ApiKeys.CountAsync(),
            TotalOllamaProviders = await dbContext.OllamaProviders.CountAsync(),
            TotalVirtualModels = await dbContext.VirtualModels.CountAsync(),
            ChatModelsCount = await dbContext.VirtualModels.CountAsync(m => m.Type == ModelType.Chat),
            EmbeddingModelsCount = await dbContext.VirtualModels.CountAsync(m => m.Type == ModelType.Embedding),
            
            RecentUsers = await dbContext.Users
                .OrderByDescending(u => u.CreationTime)
                .Take(5)
                .Select(u => new RecentUserStats
                {
                    Email = u.DisplayName,
                    CreationTime = u.CreationTime
                })
                .ToListAsync()
        };

        var providers = await dbContext.OllamaProviders
            .Include(p => p.VirtualModels)
            .ToListAsync();

        model.ProviderStats = providers.Select(p => new ProviderStats
        {
            Name = p.Name,
            ModelCount = p.VirtualModels.Count
        }).ToList();

        // API Key Stats from memory
        var allApiKeyStats = memoryUsageTracker.GetAllApiKeyStats();
        if (allApiKeyStats.Any())
        {
            var topKeyIds = allApiKeyStats.OrderByDescending(x => x.Value.TotalCalls)
                .Take(5)
                .Select(x => x.Key)
                .ToList();

            var topKeysFromDb = await dbContext.ApiKeys
                .Include(a => a.User)
                .Where(a => topKeyIds.Contains(a.Id))
                .ToListAsync();

            model.TopApiKeys = topKeysFromDb.Select(k => new TopApiKeyStats
            {
                Name = k.Name,
                UserName = k.User?.UserName ?? "Unknown",
                UsageCount = allApiKeyStats.TryGetValue(k.Id, out var stat) ? stat.TotalCalls : 0,
                LastUsed = allApiKeyStats.TryGetValue(k.Id, out var stat2) ? stat2.LastUsed : null
            })
            .OrderByDescending(x => x.UsageCount)
            .ToList();
        }

        // Top Virtual Model Stats from DB
        model.TopVirtualModels = await dbContext.VirtualModels
            .Where(v => v.UsageCount > 0)
            .OrderByDescending(v => v.UsageCount)
            .Take(5)
            .Select(v => new TopVirtualModelStats
            {
                ModelName = v.Name,
                UsageCount = v.UsageCount
            })
            .ToListAsync();

        // Live active requests from in-memory tracker — include idle models that have history
        model.ActiveRequests = activeRequestTracker.GetAll()
            .OrderByDescending(kv => kv.Value.ActiveCount > 0)
            .ThenByDescending(kv => kv.Value.LastStartedAt)
            .Select(kv => new ActiveModelInfo
            {
                ModelName = kv.Key,
                IsActive = kv.Value.ActiveCount > 0,
                ActiveCount = kv.Value.ActiveCount,
                LastQuestion = kv.Value.LastQuestion,
                BackendModelName = kv.Value.BackendModelName,
                LastStartedAt = kv.Value.LastStartedAt,
                LastCompletedAt = kv.Value.LastCompletedAt
            })
            .ToList();

        // Physical model call stats from DB
        var physicalUsages = await dbContext.UnderlyingModelUsages
            .Include(u => u.Provider)
            .OrderByDescending(u => u.UsageCount)
            .ToListAsync();

        model.PhysicalModelStats = physicalUsages.Select(u => new PhysicalModelCallStats
        {
            ModelName = u.ModelName,
            ProviderName = u.Provider?.Name ?? "Unknown",
            UsageCount = u.UsageCount
        }).ToList();

        return this.StackView(model);
    }

    [RenderInNavBar(
        NavGroupName = "Dashboard",
        NavGroupOrder = 1,
        CascadedLinksGroupName = "Monitor",
        CascadedLinksIcon = "monitor",
        CascadedLinksOrder = 1,
        LinkText = "Traffic Visualization",
        LinkOrder = 2)]
    public async Task<IActionResult> Monitor()
    {
        var model = new MonitorViewModel
        {
            VirtualModels = await dbContext.VirtualModels
                .Include(v => v.VirtualModelBackends)
                .ThenInclude(b => b.Provider)
                .ToListAsync(),
            Providers = await dbContext.OllamaProviders.ToListAsync(),
            BusyModels = activeRequestTracker.GetAll()
                .Where(kv => kv.Value.ActiveCount > 0)
                .Select(kv => kv.Key)
                .ToHashSet()
        };

        return this.StackView(model);
    }
}
