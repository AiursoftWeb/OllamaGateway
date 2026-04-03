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

        // Model Stats from memory
        var allModelStats = memoryUsageTracker.GetAllUnderlyingModelStats();
        if (allModelStats.Any())
        {
            model.TopModels = allModelStats
                .OrderByDescending(x => x.Value)
                .Take(5)
                .Select(x => new TopModelStats
                {
                    ModelName = x.Key, // format is "providerId_modelName"
                    UsageCount = x.Value
                })
                .ToList();
        }

        // Live active requests from in-memory tracker (only models with queued/running requests)
        model.ActiveRequests = activeRequestTracker.GetAll()
            .Where(kv => kv.Value.ActiveCount > 0)
            .OrderByDescending(kv => kv.Value.LastStartedAt)
            .Select(kv => new ActiveModelInfo
            {
                ModelName = kv.Key,
                ActiveCount = kv.Value.ActiveCount,
                LastQuestion = kv.Value.LastQuestion,
                LastStartedAt = kv.Value.LastStartedAt
            })
            .ToList();

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
            Providers = await dbContext.OllamaProviders.ToListAsync()
        };

        return this.StackView(model);
    }
}
