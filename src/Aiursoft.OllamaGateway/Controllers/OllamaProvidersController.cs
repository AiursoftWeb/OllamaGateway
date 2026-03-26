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
        CascadedLinksIcon = "layers",
        CascadedLinksOrder = 1,
        LinkText = "Providers",
        LinkOrder = 3)]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var providers = await dbContext.OllamaProviders
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var statusTasks = providers.Select(async p =>
        {
            var runningModels = await ollamaService.GetRunningModelsAsync(p.BaseUrl, p.BearerToken, TimeSpan.FromSeconds(3));
            return new ProviderStatus
            {
                Provider = p,
                IsAlive = runningModels != null,
                RunningModels = runningModels
            };
        });

        var statuses = (await Task.WhenAll(statusTasks)).ToList();

        var viewModel = new IndexViewModel
        {
            ProviderStatuses = statuses
        };
        return this.StackView(viewModel);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return this.StackView(new CreateViewModel());
    }

    public class TestRequest
    {
        public string? BaseUrl { get; set; }
        public string? BearerToken { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Test([FromBody] TestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Json(new { success = false, message = "URL is empty." });
        }

        var models = await ollamaService.GetUnderlyingModelsAsync(request.BaseUrl, request.BearerToken);
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
        var models = await ollamaService.GetUnderlyingModelsAsync(model.BaseUrl, model.BearerToken);
        if (models == null)
        {
            ModelState.AddModelError(nameof(model.BaseUrl), "Could not reach Ollama server at this URL. Validation failed.");
            return this.StackView(model);
        }

        var provider = new OllamaProvider
        {
            Name = model.Name,
            BaseUrl = model.BaseUrl,
            BearerToken = model.BearerToken,
            KeepAlive = model.KeepAlive
        };

        dbContext.OllamaProviders.Add(provider);
        await dbContext.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider == null) return NotFound();

        var physicalModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();

        var model = new CreateViewModel
        {
            Name = provider.Name,
            BaseUrl = provider.BaseUrl,
            BearerToken = provider.BearerToken,
            KeepAlive = provider.KeepAlive
        };
        ViewData["Id"] = id;
        ViewData["PhysicalModels"] = physicalModels;
        ViewData["WarmupModels"] = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson) ?? new List<WarmupModel>();
        return this.StackView(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleWarmup(int id, string modelName)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider == null) return NotFound();

        var warmupModels = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson) ?? new List<WarmupModel>();
        var target = warmupModels.FirstOrDefault(m => m.Name == modelName);
        if (target != null)
        {
            warmupModels.Remove(target);
        }
        else
        {
            warmupModels.Add(new WarmupModel { Name = modelName });
        }

        provider.WarmupModelsJson = System.Text.Json.JsonSerializer.Serialize(warmupModels);
        await dbContext.SaveChangesAsync();
        
        // Return 200 for AJAX, 302 for normal post
        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest" || Request.Headers["Accept"].ToString().Contains("application/json"))
        {
            return Ok();
        }
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateWarmupOptions(int id, string modelName, int? numCtx, float? temperature, float? topP, int? topK)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider == null) return NotFound();

        var warmupModels = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson) ?? new List<WarmupModel>();
        var target = warmupModels.FirstOrDefault(m => m.Name == modelName);
        if (target != null)
        {
            target.NumCtx = numCtx;
            target.Temperature = temperature;
            target.TopP = topP;
            target.TopK = topK;
            provider.WarmupModelsJson = System.Text.Json.JsonSerializer.Serialize(warmupModels);
            await dbContext.SaveChangesAsync();
        }
        return Ok();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, CreateViewModel model)
    {
        if (!ModelState.IsValid)
        {
            ViewData["Id"] = id;
            return this.StackView(model);
        }

        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider == null) return NotFound();

        provider.Name = model.Name;
        provider.BaseUrl = model.BaseUrl;
        provider.BearerToken = model.BearerToken;
        provider.KeepAlive = model.KeepAlive;

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
