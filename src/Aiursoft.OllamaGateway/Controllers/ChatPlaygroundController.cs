using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanChatWithVirtualModels)]
public class ChatPlaygroundController(
    TemplateDbContext dbContext) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Chat",
        NavGroupOrder = 100,
        CascadedLinksGroupName = "Playground",
        CascadedLinksIcon = "message-square",
        CascadedLinksOrder = 1,
        LinkText = "Playground",
        LinkOrder = 1)]
    [HttpGet]
    public async Task<IActionResult> Index(int? id)
    {
        var models = await dbContext.VirtualModels
            .Where(m => m.Type == ModelType.Chat)
            .OrderBy(m => m.Name)
            .ToListAsync();

        if (!models.Any())
        {
            return RedirectToAction("Index", "VirtualModels");
        }

        var selectedModel = id.HasValue 
            ? models.FirstOrDefault(m => m.Id == id.Value) ?? models.First()
            : models.First();

        var viewModel = new CreateViewModel // Reuse this for model info
        {
            Name = selectedModel.Name,
            UnderlyingModel = selectedModel.UnderlyingModel,
            PageTitle = "Chat Playground"
        };
        
        ViewData["ModelId"] = selectedModel.Id;
        ViewData["AllModels"] = models;
        return this.StackView(viewModel);
    }
}
