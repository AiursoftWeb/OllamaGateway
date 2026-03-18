using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageModels)]
public class VirtualModelsController(
    TemplateDbContext dbContext,
    OllamaService ollamaService) : Controller
{
    private async Task<Dictionary<int, string>> GetModelWarningsAsync(List<VirtualModel> models)
    {
        var warnings = new Dictionary<int, string>();
        var providerCache = new Dictionary<string, List<string>?>();

        foreach (var model in models)
        {
            if (model.Provider == null)
            {
                warnings[model.Id] = "Provider not found.";
                continue;
            }

            if (!providerCache.TryGetValue(model.Provider.BaseUrl, out var underlyingModels))
            {
                underlyingModels = await ollamaService.GetUnderlyingModelsAsync(model.Provider.BaseUrl);
                providerCache[model.Provider.BaseUrl] = underlyingModels;
            }

            if (underlyingModels == null)
            {
                warnings[model.Id] = "Provider offline or unreachable.";
            }
            else if (!underlyingModels.Contains(model.UnderlyingModel))
            {
                warnings[model.Id] = $"Underlying model '{model.UnderlyingModel}' missing.";
            }
        }

        return warnings;
    }

    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "message-square",
        CascadedLinksOrder = 1,
        LinkText = "Chat Models",
        LinkOrder = 1)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var models = await dbContext.VirtualModels
            .Include(m => m.Provider)
            .Where(m => m.Type == ModelType.Chat)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var viewModel = new IndexViewModel
        {
            Models = models,
            ModelWarnings = await GetModelWarningsAsync(models),
            PageTitle = "Chat Models"
        };
        return this.StackView(viewModel);
    }

    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "layers",
        CascadedLinksOrder = 1,
        LinkText = "Embedding Models",
        LinkOrder = 2)]
    [HttpGet]
    public async Task<IActionResult> EmbeddingIndex()
    {
        var models = await dbContext.VirtualModels
            .Include(m => m.Provider)
            .Where(m => m.Type == ModelType.Embedding)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var viewModel = new IndexViewModel
        {
            Models = models,
            ModelWarnings = await GetModelWarningsAsync(models),
            PageTitle = "Embedding Models"
        };
        return this.StackView(viewModel, viewName: nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Create(ModelType type = ModelType.Chat, int? providerId = null)
    {
        var providers = await dbContext.OllamaProviders.ToListAsync();
        var underlyingModels = new List<string>();
        if (providerId.HasValue)
        {
            var provider = providers.FirstOrDefault(p => p.Id == providerId);
            if (provider != null)
            {
                underlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>();
            }
        }
        else if (providers.Any())
        {
            providerId = providers.First().Id;
            underlyingModels = await ollamaService.GetUnderlyingModelsAsync(providers.First().BaseUrl) ?? new List<string>();
        }

        var model = new CreateViewModel
        {
            Type = type,
            ProviderId = providerId ?? 0,
            AvailableUnderlyingModels = underlyingModels,
            AvailableProviders = providers
        };
        return this.StackView(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>();
            }
            return this.StackView(model);
        }

        // Validate name format: lowercase, numbers, dots, hyphens, underscores and strictly one colon for tag
        if (!System.Text.RegularExpressions.Regex.IsMatch(model.Name, @"^[a-z0-9\.\-_]+:[a-zA-Z0-9\.\-_]+$"))
        {
            ModelState.AddModelError(nameof(model.Name), "The name must be lowercase and follow the pattern 'name:tag'. Only alphanumeric, dots, hyphens and underscores are allowed with exactly one colon.");
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>();
            }
            return this.StackView(model);
        }

        var virtualModel = new VirtualModel
        {
            Name = model.Name,
            UnderlyingModel = model.UnderlyingModel,
            ProviderId = model.ProviderId,
            Type = model.Type,
            Thinking = model.Thinking,
            NumCtx = model.NumCtx,
            Temperature = model.Temperature,
            TopP = model.TopP,
            TopK = model.TopK,
            NumPredict = model.NumPredict,
            RepeatPenalty = model.RepeatPenalty,
            UseRawOutput = model.UseRawOutput,
            KeepAlive = model.KeepAlive
        };

        dbContext.VirtualModels.Add(virtualModel);
        await dbContext.SaveChangesAsync();

        return virtualModel.Type == ModelType.Chat ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(EmbeddingIndex));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var virtualModel = await dbContext.VirtualModels.FindAsync(id);
        if (virtualModel == null)
        {
            return NotFound();
        }

        var providers = await dbContext.OllamaProviders.ToListAsync();
        var provider = providers.FirstOrDefault(p => p.Id == virtualModel.ProviderId);
        var underlyingModels = provider != null ? (await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>()) : new List<string>();

        var model = new CreateViewModel // Use same for edit for simplicity
        {
            Name = virtualModel.Name,
            UnderlyingModel = virtualModel.UnderlyingModel,
            ProviderId = virtualModel.ProviderId,
            Type = virtualModel.Type,
            Thinking = virtualModel.Thinking,
            NumCtx = virtualModel.NumCtx,
            Temperature = virtualModel.Temperature,
            TopP = virtualModel.TopP,
            TopK = virtualModel.TopK,
            NumPredict = virtualModel.NumPredict,
            RepeatPenalty = virtualModel.RepeatPenalty,
            UseRawOutput = virtualModel.UseRawOutput,
            KeepAlive = virtualModel.KeepAlive,
            AvailableUnderlyingModels = underlyingModels,
            AvailableProviders = providers
        };
        ViewData["Id"] = id;
        return this.StackView(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>();
            }
            ViewData["Id"] = id;
            return this.StackView(model);
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(model.Name, @"^[a-z0-9\.\-_]+:[a-zA-Z0-9\.\-_]+$"))
        {
            ModelState.AddModelError(nameof(model.Name), "The name must be lowercase and follow the pattern 'name:tag'. Only alphanumeric, dots, hyphens and underscores are allowed with exactly one colon.");
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl) ?? new List<string>();
            }
            ViewData["Id"] = id;
            return this.StackView(model);
        }

        var virtualModel = await dbContext.VirtualModels.FindAsync(id);
        if (virtualModel == null)
        {
            return NotFound();
        }

        virtualModel.Name = model.Name;
        virtualModel.UnderlyingModel = model.UnderlyingModel;
        virtualModel.ProviderId = model.ProviderId;
        virtualModel.Type = model.Type;
        virtualModel.Thinking = model.Thinking;
        virtualModel.NumCtx = model.NumCtx;
        virtualModel.Temperature = model.Temperature;
        virtualModel.TopP = model.TopP;
        virtualModel.TopK = model.TopK;
        virtualModel.NumPredict = model.NumPredict;
        virtualModel.RepeatPenalty = model.RepeatPenalty;
        virtualModel.UseRawOutput = model.UseRawOutput;
        virtualModel.KeepAlive = model.KeepAlive;

        await dbContext.SaveChangesAsync();

        return virtualModel.Type == ModelType.Chat ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(EmbeddingIndex));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var virtualModel = await dbContext.VirtualModels.FindAsync(id);
        if (virtualModel != null)
        {
            var type = virtualModel.Type;
            dbContext.VirtualModels.Remove(virtualModel);
            await dbContext.SaveChangesAsync();
            return type == ModelType.Chat ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(EmbeddingIndex));
        }
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = AppPermissionNames.CanChatWithVirtualModels)]
    [HttpGet]
    public async Task<IActionResult> Chat(int id)
    {
        var virtualModel = await dbContext.VirtualModels
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (virtualModel == null || virtualModel.Type != ModelType.Chat)
        {
            return NotFound();
        }

        var viewModel = new CreateViewModel // Reuse this for model info
        {
            Name = virtualModel.Name,
            UnderlyingModel = virtualModel.UnderlyingModel,
            PageTitle = $"Chat with {virtualModel.Name}"
        };
        ViewData["ModelId"] = id;
        return this.StackView(viewModel);
    }
}
