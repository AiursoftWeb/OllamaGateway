using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models.OllamaProvidersViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[Authorize(Policy = AppPermissionNames.CanManageProviders)]
public class OllamaProvidersController(
    TemplateDbContext dbContext,
    OllamaService ollamaService) : Controller
{
    [RenderInNavBar(
        NavGroupName = "Ollama Gateway",
        NavGroupOrder = 9000,
        CascadedLinksGroupName = "Gateway",
        CascadedLinksIcon = "server",
        CascadedLinksOrder = 1,
        LinkText = "Providers",
        LinkOrder = 3)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var providers = await dbContext.OllamaProviders
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var viewModel = new IndexViewModel
        {
            Providers = providers
        };
        return this.StackView(viewModel);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return this.StackView(new CreateViewModel());
    }

    [HttpPost]
    public async Task<IActionResult> Test([FromBody] string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Json(new { success = false, message = "URL is empty." });
        }

        var models = await ollamaService.GetUnderlyingModelsAsync(baseUrl);
        if (models == null)
        {
            return Json(new { success = false, message = "Failed to connect to Ollama. Ensure the URL is correct and Ollama is running." });
        }

        return Json(new { success = true, models });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return this.StackView(model);
        }

        if (!Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ModelState.AddModelError(nameof(model.BaseUrl), "The Base URL must be a valid HTTP or HTTPS absolute URL.");
            return this.StackView(model);
        }

        // Mandatory verification before saving
        var models = await ollamaService.GetUnderlyingModelsAsync(model.BaseUrl);
        if (models == null)
        {
            ModelState.AddModelError(nameof(model.BaseUrl), "Could not reach Ollama server at this URL. Validation failed.");
            return this.StackView(model);
        }

        var provider = new OllamaProvider
        {
            Name = model.Name,
            BaseUrl = model.BaseUrl
        };

        dbContext.OllamaProviders.Add(provider);
        await dbContext.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider != null)
        {
            dbContext.OllamaProviders.Remove(provider);
            await dbContext.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index));
    }
}
