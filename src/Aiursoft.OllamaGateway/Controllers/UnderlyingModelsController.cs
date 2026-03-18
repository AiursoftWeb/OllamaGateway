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
    OllamaService ollamaService) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "database",
        CascadedLinksOrder = 1,
        LinkText = "Physical Models",
        LinkOrder = 4)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var providers = await dbContext.OllamaProviders.ToListAsync();
        var virtualModels = await dbContext.VirtualModels.ToListAsync();
        var viewModel = new IndexViewModel();

        foreach (var provider in providers)
        {
            var rawModels = await ollamaService.GetDetailedModelsAsync(provider.BaseUrl);
            var runningModels = await ollamaService.GetRunningModelsAsync(provider.BaseUrl);

            if (rawModels != null)
            {
                foreach (var raw in rawModels)
                {
                    viewModel.Models.Add(new UnderlyingModelInfo
                    {
                        Provider = provider,
                        RawModel = raw,
                        IsRunning = runningModels?.Any(r => r.Name == raw.Name) ?? false,
                        UsedByVirtualModels = virtualModels
                            .Where(v => v.ProviderId == provider.Id && v.UnderlyingModel == raw.Name)
                            .ToList()
                    });
                }
            }
        }

        return this.StackView(viewModel);
    }
}
