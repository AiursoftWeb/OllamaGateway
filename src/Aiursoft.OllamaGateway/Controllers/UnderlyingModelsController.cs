using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.UnderlyingModelsViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageProviders)]
public class UnderlyingModelsController(
    TemplateDbContext dbContext,
    OllamaService ollamaService,
    MemoryUsageTracker memoryUsageTracker) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "layers",
        CascadedLinksOrder = 1,
        LinkText = "Physical Models",
        LinkOrder = 4)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var providers = await dbContext.OllamaProviders.ToListAsync();
        var virtualModels = await dbContext.VirtualModels.Include(m => m.VirtualModelBackends).ToListAsync();
        var viewModel = new IndexViewModel();

        foreach (var provider in providers)
        {
            if (provider.ProviderType == ProviderType.OpenAI)
            {
                var openAiModels = await ollamaService.GetOpenAIAvailableModelsAsync(provider.BaseUrl, provider.BearerToken);
                if (openAiModels != null)
                {
                    foreach (var modelName in openAiModels)
                    {
                        viewModel.Models.Add(new UnderlyingModelInfo
                        {
                            Provider = provider,
                            RawModel = new OllamaService.OllamaModel { Name = modelName, Model = modelName },
                            IsRunning = true,
                            TotalCalls = memoryUsageTracker.GetUnderlyingModelStats(provider.Id, modelName),
                            UsedByVirtualModels = virtualModels
                                .Where(v => v.VirtualModelBackends.Any(b => b.ProviderId == provider.Id && b.UnderlyingModelName == modelName))
                                .ToList()
                        });
                    }
                }
                continue;
            }

            var rawModels = await ollamaService.GetDetailedModelsAsync(provider.BaseUrl, provider.BearerToken);
            var runningModels = await ollamaService.GetRunningModelsAsync(provider.BaseUrl, provider.BearerToken);

            if (rawModels != null)
            {
                foreach (var raw in rawModels)
                {
                    viewModel.Models.Add(new UnderlyingModelInfo
                    {
                        Provider = provider,
                        RawModel = raw,
                        IsRunning = runningModels?.Any(r => r.Name == raw.Name) ?? false,
                        TotalCalls = memoryUsageTracker.GetUnderlyingModelStats(provider.Id, raw.Name),
                        UsedByVirtualModels = virtualModels
                            .Where(v => v.VirtualModelBackends.Any(b => b.ProviderId == provider.Id && b.UnderlyingModelName == raw.Name))
                            .ToList()
                    });
                }
            }
        }

        return this.StackView(viewModel);
    }
}
