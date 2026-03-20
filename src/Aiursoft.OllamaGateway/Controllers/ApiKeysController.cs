using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.ApiKeysViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Aiursoft.OllamaGateway.Authorization;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageApiKeys)]
public class ApiKeysController(
    TemplateDbContext dbContext,
    UserManager<User> userManager,
    MemoryUsageTracker memoryUsageTracker,
    GlobalSettingsService globalSettingsService) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Settings",
        NavGroupOrder = 9998,
        CascadedLinksGroupName = "Personal",
        CascadedLinksIcon = "user",
        CascadedLinksOrder = 1,
        LinkText = "API Keys",
        LinkOrder = 2)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await userManager.GetUserAsync(User);
        var keys = await dbContext.ApiKeys
            .Where(k => k.UserId == user!.Id)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();

        var model = new IndexViewModel
        {
            ApiKeys = keys,
            NewKey = TempData["NewKey"] as string
        };
        
        foreach (var key in keys)
        {
            var stats = memoryUsageTracker.GetApiKeyStats(key.Id);
            model.LastUsedTimes[key.Id] = stats.LastUsed;
            model.TotalCalls[key.Id] = stats.TotalCalls;
        }
        
        return this.StackView(model);
    }

    [HttpGet]
    public async Task<IActionResult> Usage(int id)
    {
        var user = await userManager.GetUserAsync(User);
        var key = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == user!.Id);

        if (key == null)
        {
            return NotFound();
        }

        var model = new UsageViewModel
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
    public IActionResult Create()
    {
        return this.StackView(new CreateViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return this.StackView(model);
        }

        var user = await userManager.GetUserAsync(User);
        var keyStr = Guid.NewGuid().ToString("N");
        var key = new ApiKey
        {
            Name = model.Name,
            Key = keyStr,
            UserId = user!.Id
        };

        dbContext.ApiKeys.Add(key);
        await dbContext.SaveChangesAsync();

        TempData["NewKey"] = keyStr;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await userManager.GetUserAsync(User);
        var key = await dbContext.ApiKeys
            .FirstOrDefaultAsync(k => k.Id == id && k.UserId == user!.Id);

        if (key != null)
        {
            dbContext.ApiKeys.Remove(key);
            await dbContext.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Index));
    }
}
