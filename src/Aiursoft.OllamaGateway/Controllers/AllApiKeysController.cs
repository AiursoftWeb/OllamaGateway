using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using AllApiKeysIndexViewModel = Aiursoft.OllamaGateway.Models.AllApiKeysViewModels.IndexViewModel;
using ApiKeysEditViewModel = Aiursoft.OllamaGateway.Models.ApiKeysViewModels.EditViewModel;
using ApiKeysUsageViewModel = Aiursoft.OllamaGateway.Models.ApiKeysViewModels.UsageViewModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageAnyApiKey)]
public class AllApiKeysController(
    TemplateDbContext dbContext,
    MemoryUsageTracker memoryUsageTracker,
    GlobalSettingsService globalSettingsService) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Administration",
        NavGroupOrder = 9999,
        CascadedLinksGroupName = "Directory",
        CascadedLinksIcon = "users",
        CascadedLinksOrder = 9998,
        LinkText = "All API Keys",
        LinkOrder = 4)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var keys = await dbContext.ApiKeys
            .Include(k => k.User)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var model = new AllApiKeysIndexViewModel
        {
            ApiKeys = keys
        };

        foreach (var key in keys)
        {
            model.CreatorDisplayNames[key.Id] = key.User?.DisplayName ?? "Unknown";

            var stats = memoryUsageTracker.GetApiKeyStats(key.Id);
            model.LastUsedTimes[key.Id] = stats.LastUsed;
            model.TotalCalls[key.Id] = stats.TotalCalls;

            var breakdown = memoryUsageTracker.GetApiKeyModelBreakdown(key.Id);
            if (breakdown.Count > 0)
            {
                var topModel = breakdown.First();
                model.TopModels[key.Id] = topModel.Key;
                var totalInMemory = breakdown.Values.Sum();
                model.TopModelPercentages[key.Id] = totalInMemory > 0
                    ? (double)topModel.Value / totalInMemory * 100.0
                    : 0;
            }
        }

        return this.StackView(model);
    }

    [HttpGet]
    public async Task<IActionResult> Usage(int id)
    {
        var key = await dbContext.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.Id == id);

        if (key == null)
        {
            return NotFound();
        }

        var model = new ApiKeysUsageViewModel
        {
            ApiKey = key.Key[..4] + "...",
            ApiKeyName = key.Name,
            BaseUrl = $"{Request.Scheme}://{Request.Host}",
            DefaultChatModel = await globalSettingsService.GetDefaultChatModelAsync(),
            DefaultEmbeddingModel = await globalSettingsService.GetDefaultEmbeddingModelAsync()
        };

        return this.StackView(model);
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var key = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id);

        if (key == null)
        {
            return NotFound();
        }

        return this.StackView(new ApiKeysEditViewModel
        {
            Id = key.Id,
            Name = key.Name,
            ExpirationTime = key.ExpirationTime,
            MaxRequests = key.MaxRequests,
            TimeWindowSeconds = key.TimeWindowSeconds,
            RateLimitEnabled = key.RateLimitEnabled,
            RateLimitHang = key.RateLimitHang
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ApiKeysEditViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return this.StackView(model);
        }

        var key = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == model.Id);

        if (key == null)
        {
            return NotFound();
        }

        key.Name = model.Name;
        key.ExpirationTime = model.ExpirationTime;
        key.MaxRequests = model.MaxRequests;
        key.TimeWindowSeconds = model.TimeWindowSeconds;
        key.RateLimitEnabled = model.RateLimitEnabled;
        key.RateLimitHang = model.RateLimitHang;
        await dbContext.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var key = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id);

        if (key != null)
        {
            dbContext.ApiKeys.Remove(key);
            await dbContext.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index));
    }
}
