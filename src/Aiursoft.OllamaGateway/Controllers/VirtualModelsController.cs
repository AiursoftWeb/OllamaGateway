using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
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
            if (!model.VirtualModelBackends.Any())
            {
                warnings[model.Id] = "No backends configured.";
                continue;
            }

            var modelWarnings = new List<string>();
            foreach (var backend in model.VirtualModelBackends)
            {
                if (backend.Provider == null)
                {
                    modelWarnings.Add($"Backend {backend.Id}: Provider not found.");
                    continue;
                }

                if (!providerCache.TryGetValue($"{backend.Provider.BaseUrl}_{backend.Provider.BearerToken}", out var underlyingModels))
                {
                    underlyingModels = await ollamaService.GetUnderlyingModelsAsync(backend.Provider.BaseUrl, backend.Provider.BearerToken);
                    providerCache[$"{backend.Provider.BaseUrl}_{backend.Provider.BearerToken}"] = underlyingModels;
                }

                if (underlyingModels == null)
                {
                    modelWarnings.Add($"Backend {backend.Id}: Provider offline or unreachable.");
                }
                else if (!underlyingModels.Contains(backend.UnderlyingModelName))
                {
                    modelWarnings.Add($"Backend {backend.Id}: Underlying model '{backend.UnderlyingModelName}' missing.");
                }
            }
            
            if (modelWarnings.Any())
            {
                warnings[model.Id] = string.Join(" ", modelWarnings);
            }
        }

        return warnings;
    }

    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "layers",
        CascadedLinksOrder = 1,
        LinkText = "Chat Models",
        LinkOrder = 1)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var models = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends)
            .ThenInclude(b => b.Provider)
            .Where(m => m.Type == ModelType.Chat)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var viewModel = new IndexViewModel
        {
            Models = models,
            ModelWarnings = await GetModelWarningsAsync(models),
            PageTitle = "Chat Models",
            CurrentType = ModelType.Chat
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
            .Include(m => m.VirtualModelBackends)
            .ThenInclude(b => b.Provider)
            .Where(m => m.Type == ModelType.Embedding)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var viewModel = new IndexViewModel
        {
            Models = models,
            ModelWarnings = await GetModelWarningsAsync(models),
            PageTitle = "Embedding Models",
            CurrentType = ModelType.Embedding
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
                underlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();
            }
        }
        else if (providers.Any())
        {
            var firstProvider = providers.First();
            providerId = firstProvider.Id;
            underlyingModels = await ollamaService.GetUnderlyingModelsAsync(firstProvider.BaseUrl, firstProvider.BearerToken) ?? new List<string>();
        }

        var model = new CreateViewModel
        {
            Type = type,
            ProviderId = providerId ?? 0,
            AvailableUnderlyingModels = underlyingModels,
            AvailableProviders = providers
        };
        ViewData["ThinkingOptions"] = new List<SelectListItem>
        {
            new() { Value = "", Text = "Default", Selected = true },
            new() { Value = "true", Text = "Enabled" },
            new() { Value = "false", Text = "Disabled" }
        };
        return this.StackView(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateViewModel model)
    {
        if (!ModelState.IsValid || string.IsNullOrEmpty(model.UnderlyingModel) || model.ProviderId == 0)
        {
            if (string.IsNullOrEmpty(model.UnderlyingModel)) ModelState.AddModelError(nameof(model.UnderlyingModel), "The Underlying Model field is required.");
            if (model.ProviderId == 0) ModelState.AddModelError(nameof(model.ProviderId), "The Provider field is required.");
            
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();
            }
            ViewData["ThinkingOptions"] = new List<SelectListItem>
            {
                new() { Value = "", Text = "Default", Selected = model.Thinking == null },
                new() { Value = "true", Text = "Enabled", Selected = model.Thinking == true },
                new() { Value = "false", Text = "Disabled", Selected = model.Thinking == false }
            };
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
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();
            }
            ViewData["ThinkingOptions"] = new List<SelectListItem>
            {
                new() { Value = "", Text = "Default", Selected = model.Thinking == null },
                new() { Value = "true", Text = "Enabled", Selected = model.Thinking == true },
                new() { Value = "false", Text = "Disabled", Selected = model.Thinking == false }
            };
            return this.StackView(model);
        }

        var virtualModel = new VirtualModel
        {
            Name = model.Name,
            Type = model.Type,
            SelectionStrategy = model.SelectionStrategy,
            MaxRetries = model.MaxRetries,
            HealthCheckTimeout = model.HealthCheckTimeout,
            Thinking = model.Thinking,
            NumCtx = model.NumCtx,
            Temperature = model.Temperature,
            TopP = model.TopP,
            TopK = model.TopK,
            NumPredict = model.NumPredict,
            RepeatPenalty = model.RepeatPenalty,
            UseRawOutput = model.UseRawOutput,
            VirtualModelBackends =
            [
                new VirtualModelBackend
                {
                    UnderlyingModelName = model.UnderlyingModel,
                    ProviderId = model.ProviderId,
                    Enabled = true,
                    IsHealthy = true,
                    Priority = 1,
                    Weight = 1
                }
            ]
        };

        dbContext.VirtualModels.Add(virtualModel);
        await dbContext.SaveChangesAsync();

        return virtualModel.Type == ModelType.Chat ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(EmbeddingIndex));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id, int? providerId = null)
    {
        var virtualModel = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends)
            .FirstOrDefaultAsync(m => m.Id == id);
            
        if (virtualModel == null)
        {
            return NotFound();
        }

        var firstBackend = virtualModel.VirtualModelBackends.FirstOrDefault();

        var providers = await dbContext.OllamaProviders.ToListAsync();
        providerId ??= firstBackend?.ProviderId;
        
        var provider = providers.FirstOrDefault(p => p.Id == providerId);
        var underlyingModels = provider != null ? (await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>()) : new List<string>();

        var model = new CreateViewModel // Use same for edit for simplicity
        {
            Name = virtualModel.Name,
            UnderlyingModel = firstBackend?.UnderlyingModelName ?? string.Empty,
            ProviderId = providerId ?? 0,
            Type = virtualModel.Type,
            SelectionStrategy = virtualModel.SelectionStrategy,
            MaxRetries = virtualModel.MaxRetries,
            HealthCheckTimeout = virtualModel.HealthCheckTimeout,
            Thinking = virtualModel.Thinking,
            NumCtx = virtualModel.NumCtx,
            Temperature = virtualModel.Temperature,
            TopP = virtualModel.TopP,
            TopK = virtualModel.TopK,
            NumPredict = virtualModel.NumPredict,
            RepeatPenalty = virtualModel.RepeatPenalty,
            UseRawOutput = virtualModel.UseRawOutput,
            AvailableUnderlyingModels = underlyingModels,
            AvailableProviders = providers
        };
        ViewData["Id"] = id;
        ViewData["Backends"] = virtualModel.VirtualModelBackends;
        ViewData["ThinkingOptions"] = new List<SelectListItem>
        {
            new() { Value = "", Text = "Default", Selected = virtualModel.Thinking == null },
            new() { Value = "true", Text = "Enabled", Selected = virtualModel.Thinking == true },
            new() { Value = "false", Text = "Disabled", Selected = virtualModel.Thinking == false }
        };
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
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();
            }
            ViewData["Id"] = id;
            var vm = await dbContext.VirtualModels.Include(m => m.VirtualModelBackends).FirstOrDefaultAsync(m => m.Id == id);
            ViewData["Backends"] = vm?.VirtualModelBackends;
            ViewData["ThinkingOptions"] = new List<SelectListItem>
            {
                new() { Value = "", Text = "Default", Selected = model.Thinking == null },
                new() { Value = "true", Text = "Enabled", Selected = model.Thinking == true },
                new() { Value = "false", Text = "Disabled", Selected = model.Thinking == false }
            };
            return this.StackView(model);
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(model.Name, @"^[a-z0-9\.\-_]+:[a-zA-Z0-9\.\-_]+$"))
        {
            ModelState.AddModelError(nameof(model.Name), "The name must be lowercase and follow the pattern 'name:tag'. Only alphanumeric, dots, hyphens and underscores are allowed with exactly one colon.");
            model.AvailableProviders = await dbContext.OllamaProviders.ToListAsync();
            var provider = model.AvailableProviders.FirstOrDefault(p => p.Id == model.ProviderId);
            if (provider != null)
            {
                model.AvailableUnderlyingModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();
            }
            ViewData["Id"] = id;
            var vm = await dbContext.VirtualModels.Include(m => m.VirtualModelBackends).FirstOrDefaultAsync(m => m.Id == id);
            ViewData["Backends"] = vm?.VirtualModelBackends;
            ViewData["ThinkingOptions"] = new List<SelectListItem>
            {
                new() { Value = "", Text = "Default", Selected = model.Thinking == null },
                new() { Value = "true", Text = "Enabled", Selected = model.Thinking == true },
                new() { Value = "false", Text = "Disabled", Selected = model.Thinking == false }
            };
            return this.StackView(model);
        }

        var virtualModel = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends)
            .FirstOrDefaultAsync(m => m.Id == id);
            
        if (virtualModel == null)
        {
            return NotFound();
        }

        virtualModel.Name = model.Name;
        virtualModel.Type = model.Type;
        virtualModel.SelectionStrategy = model.SelectionStrategy;
        virtualModel.MaxRetries = model.MaxRetries;
        virtualModel.HealthCheckTimeout = model.HealthCheckTimeout;
        virtualModel.Thinking = model.Thinking;
        virtualModel.NumCtx = model.NumCtx;
        virtualModel.Temperature = model.Temperature;
        virtualModel.TopP = model.TopP;
        virtualModel.TopK = model.TopK;
        virtualModel.NumPredict = model.NumPredict;
        virtualModel.RepeatPenalty = model.RepeatPenalty;
        virtualModel.UseRawOutput = model.UseRawOutput;

        await dbContext.SaveChangesAsync();

        return virtualModel.Type == ModelType.Chat ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(EmbeddingIndex));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBackend(int id, int providerId, string underlyingModel, int priority = 1, int weight = 1)
    {
        var virtualModel = await dbContext.VirtualModels.FindAsync(id);
        if (virtualModel == null) return NotFound();

        dbContext.VirtualModelBackends.Add(new VirtualModelBackend
        {
            VirtualModelId = id,
            ProviderId = providerId,
            UnderlyingModelName = underlyingModel,
            Priority = priority,
            Weight = weight,
            Enabled = true,
            IsHealthy = true
        });

        await dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateBackend(int id, int priority, int weight)
    {
        var backend = await dbContext.VirtualModelBackends.FindAsync(id);
        if (backend == null) return NotFound();

        backend.Priority = priority;
        backend.Weight = weight;
        await dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id = backend.VirtualModelId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleBackend(int id)
    {
        var backend = await dbContext.VirtualModelBackends.FindAsync(id);
        if (backend == null) return NotFound();

        backend.Enabled = !backend.Enabled;
        await dbContext.SaveChangesAsync();
        return RedirectToAction(nameof(Edit), new { id = backend.VirtualModelId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteBackend(int id)
    {
        var backend = await dbContext.VirtualModelBackends.FindAsync(id);
        if (backend != null)
        {
            var modelId = backend.VirtualModelId;
            dbContext.VirtualModelBackends.Remove(backend);
            await dbContext.SaveChangesAsync();
            return RedirectToAction(nameof(Edit), new { id = modelId });
        }
        return BadRequest();
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
            .Include(m => m.VirtualModelBackends)
            .ThenInclude(b => b.Provider)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (virtualModel == null || virtualModel.Type != ModelType.Chat)
        {
            return NotFound();
        }

        var firstBackend = virtualModel.VirtualModelBackends.FirstOrDefault();

        var viewModel = new CreateViewModel // Reuse this for model info
        {
            Name = virtualModel.Name,
            UnderlyingModel = firstBackend?.UnderlyingModelName ?? string.Empty,
            PageTitle = $"Chat with {virtualModel.Name}"
        };
        ViewData["ModelId"] = id;
        return this.StackView(viewModel);
    }
}
