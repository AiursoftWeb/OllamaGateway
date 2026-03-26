using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.ChatPlaygroundViewModels;
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
            .Include(m => m.VirtualModelBackends)
            .AsNoTracking()
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var viewModel = new IndexViewModel
        {
            Name = selectedModel.Name,
            UnderlyingModel = selectedModel.VirtualModelBackends.FirstOrDefault()?.UnderlyingModelName ?? string.Empty,
            ModelId = selectedModel.Id,
            AllModels = models,
            BaseUrl = baseUrl,
            PageTitle = "Chat Playground",
            Thinking = selectedModel.Thinking,
            NumCtx = selectedModel.NumCtx,
            Temperature = selectedModel.Temperature,
            TopP = selectedModel.TopP,
            TopK = selectedModel.TopK
        };

        return this.StackView(viewModel);
    }

    [RenderInNavBar(
        NavGroupName = "Chat",
        NavGroupOrder = 100,
        CascadedLinksGroupName = "Playground",
        CascadedLinksIcon = "message-square",
        CascadedLinksOrder = 1,
        LinkText = "Embedding Lab",
        LinkOrder = 2)]
    [HttpGet]
    public async Task<IActionResult> EmbeddingLab(int? id)
    {
        var models = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends)
            .AsNoTracking()
            .Where(m => m.Type == ModelType.Embedding)
            .OrderBy(m => m.Name)
            .ToListAsync();

        if (!models.Any())
        {
            return RedirectToAction("EmbeddingIndex", "VirtualModels");
        }

        var selectedModel = id.HasValue
            ? models.FirstOrDefault(m => m.Id == id.Value) ?? models.First()
            : models.First();

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var viewModel = new IndexViewModel
        {
            Name = selectedModel.Name,
            UnderlyingModel = selectedModel.VirtualModelBackends.FirstOrDefault()?.UnderlyingModelName ?? string.Empty,
            ModelId = selectedModel.Id,
            AllModels = models,
            BaseUrl = baseUrl,
            PageTitle = "Embedding Lab",
            Thinking = selectedModel.Thinking,
            NumCtx = selectedModel.NumCtx,
            Temperature = selectedModel.Temperature,
            TopP = selectedModel.TopP,
            TopK = selectedModel.TopK
        };

        return this.StackView(viewModel);
    }

    [RenderInNavBar(
        NavGroupName = "Chat",
        NavGroupOrder = 100,
        CascadedLinksGroupName = "Playground",
        CascadedLinksIcon = "message-square",
        CascadedLinksOrder = 1,
        LinkText = "Completion Lab",
        LinkOrder = 3)]
    [HttpGet]
    public async Task<IActionResult> CompletionLab(int? id)
    {
        var models = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends)
            .AsNoTracking()
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

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var viewModel = new IndexViewModel
        {
            Name = selectedModel.Name,
            UnderlyingModel = selectedModel.VirtualModelBackends.FirstOrDefault()?.UnderlyingModelName ?? string.Empty,
            ModelId = selectedModel.Id,
            AllModels = models,
            BaseUrl = baseUrl,
            PageTitle = "Completion Lab",
            Thinking = selectedModel.Thinking,
            NumCtx = selectedModel.NumCtx,
            Temperature = selectedModel.Temperature,
            TopP = selectedModel.TopP,
            TopK = selectedModel.TopK
        };

        return this.StackView(viewModel);
    }

    [Authorize(Policy = AppPermissionNames.CanChatWithUnderlyingModels)]
    [HttpGet]
    public async Task<IActionResult> PhysicalChat(
        int providerId, 
        string modelName,
        bool? thinking,
        int? numCtx,
        float? temperature,
        float? topP,
        int? topK)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(providerId);
        if (provider == null)
        {
            return NotFound();
        }

        var models = await dbContext.VirtualModels
            .AsNoTracking()
            .Where(m => m.Type == ModelType.Chat)
            .OrderBy(m => m.Name)
            .ToListAsync();

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var physicalModelName = $"physical_{providerId}_{modelName}";
        
        var viewModel = new IndexViewModel
        {
            Name = physicalModelName,
            UnderlyingModel = modelName,
            ModelId = -1,
            AllModels = models,
            BaseUrl = baseUrl,
            PageTitle = $"Physical Model: {modelName}",
            Thinking = thinking,
            NumCtx = numCtx,
            Temperature = temperature,
            TopP = topP,
            TopK = topK
        };

        return this.StackView(viewModel);
    }
}
