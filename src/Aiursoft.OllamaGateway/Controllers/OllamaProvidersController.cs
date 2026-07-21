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
    OllamaService ollamaService,
    IModelSelector modelSelector,
    ILogger<OllamaProvidersController> logger) : Controller
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
            if (p.ProviderType == ProviderType.OpenAI)
            {
                var oaiModels = await ollamaService.GetOpenAIAvailableModelsAsync(p.BaseUrl, p.BearerToken);
                return new ProviderStatus
                {
                    Provider = p,
                    IsAlive = oaiModels != null,
                    Version = "OpenAI API",
                    RunningModels = oaiModels?.Select(m => new OllamaService.OllamaRunningModel { Name = m, Model = m }).ToList()
                };
            }

            var runningModels = await ollamaService.GetRunningModelsAsync(p.BaseUrl, p.BearerToken, timeoutSeconds: 3);
            var version = await ollamaService.GetVersionAsync(p.BaseUrl, p.BearerToken);
            return new ProviderStatus
            {
                Provider = p,
                IsAlive = runningModels != null,
                Version = version,
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
        public int? ProviderId { get; set; }
        public string? BaseUrl { get; set; }
        public string? BearerToken { get; set; }
        public ProviderType ProviderType { get; set; } = ProviderType.Ollama;
    }

    [HttpPost]
    public async Task<IActionResult> Test([FromBody] TestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            return Json(new { success = false, message = "URL is empty." });
        }

        List<string>? models;
        if (request.ProviderType == ProviderType.OpenAI)
        {
            models = await ollamaService.GetOpenAIAvailableModelsAsync(request.BaseUrl, request.BearerToken);
        }
        else
        {
            models = await ollamaService.GetUnderlyingModelsAsync(request.BaseUrl, request.BearerToken);
        }

        if (models == null)
        {
            return Json(new { success = false, message = "Failed to connect to the provider or parse models." });
        }

        // Test succeeded — revive the provider if this is an existing one (editing, not creating)
        if (request.ProviderId.HasValue)
        {
            var backends = await dbContext.VirtualModelBackends
                .Where(b => b.ProviderId == request.ProviderId.Value)
                .ToListAsync();

            foreach (var backend in backends)
            {
                backend.IsHealthy = true;
                backend.IsReady = true;
                modelSelector.ReportSuccess(backend.Id);
            }

            if (backends.Any())
            {
                await dbContext.SaveChangesAsync();
                logger.LogInformation(
                    "Provider {ProviderId} tested successfully — manually revived: {Count} backends marked healthy and un-banned",
                    request.ProviderId.Value, backends.Count);
            }
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
        if (model.ProviderType == ProviderType.OpenAI)
        {
            var oaiModels = await ollamaService.GetOpenAIAvailableModelsAsync(model.BaseUrl, model.BearerToken);
            if (oaiModels == null)
            {
                ModelState.AddModelError(nameof(model.BaseUrl), "Could not reach OpenAI-compatible server at this URL. Validation failed.");
                return this.StackView(model);
            }
        }
        else
        {
            var ollamaModels = await ollamaService.GetUnderlyingModelsAsync(model.BaseUrl, model.BearerToken);
            if (ollamaModels == null)
            {
                ModelState.AddModelError(nameof(model.BaseUrl), "Could not reach Ollama server at this URL. Validation failed.");
                return this.StackView(model);
            }
        }

        var provider = new OllamaProvider
        {
            Name = model.Name,
            BaseUrl = model.BaseUrl,
            BearerToken = model.BearerToken,
            KeepAlive = model.KeepAlive,
            ProviderType = model.ProviderType,
            MaxParallelism = model.MaxParallelism,
            HealthCheckTimeoutSeconds = model.HealthCheckTimeoutSeconds
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

        List<string> physicalModels;
        if (provider.ProviderType == ProviderType.OpenAI)
            physicalModels = new List<string>(); // warmup not applicable to OpenAI providers
        else
            physicalModels = await ollamaService.GetUnderlyingModelsAsync(provider.BaseUrl, provider.BearerToken) ?? new List<string>();

        var model = new CreateViewModel
        {
            Name = provider.Name,
            BaseUrl = provider.BaseUrl,
            BearerToken = provider.BearerToken,
            KeepAlive = provider.KeepAlive,
            ProviderType = provider.ProviderType,
            MaxParallelism = provider.MaxParallelism,
            HealthCheckTimeoutSeconds = provider.HealthCheckTimeoutSeconds
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
    public async Task<IActionResult> UpdateWarmupOptions(int id, string modelName, int? numCtx, bool? isEmbedding, int? timeoutSeconds)
    {
        var provider = await dbContext.OllamaProviders.FindAsync(id);
        if (provider == null) return NotFound();

        var warmupModels = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson) ?? new List<WarmupModel>();
        var target = warmupModels.FirstOrDefault(m => m.Name == modelName);
        if (target != null)
        {
            target.NumCtx = numCtx;
            target.IsEmbedding = isEmbedding ?? false;
            target.TimeoutSeconds = timeoutSeconds;
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
        if (!string.IsNullOrWhiteSpace(model.BearerToken))
        {
            provider.BearerToken = model.BearerToken;
        }
        provider.KeepAlive = model.KeepAlive;
        provider.ProviderType = model.ProviderType;
        provider.MaxParallelism = model.MaxParallelism;
        provider.HealthCheckTimeoutSeconds = model.HealthCheckTimeoutSeconds;

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
