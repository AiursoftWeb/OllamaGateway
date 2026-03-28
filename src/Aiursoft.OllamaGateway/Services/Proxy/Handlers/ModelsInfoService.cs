using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.Proxy.Handlers;

public interface IModelsInfoService : IScopedDependency
{
    Task<IActionResult> GetTagsAsync();
    Task<IActionResult> GetPsAsync();
    Task<IActionResult> GetVersionAsync();
    Task<IActionResult> GetOpenAIModelsAsync();
}

public class ModelsInfoService(
    TemplateDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    OllamaService ollamaService) : IModelsInfoService
{
    private static readonly JsonSerializerOptions OllamaJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<IActionResult> GetTagsAsync()
    {
        var virtualModels = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
            .ToListAsync();
        
        var allTags = new List<OllamaService.OllamaModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaModel>?>();

        foreach (var vm in virtualModels)
        {
            var backend = vm.VirtualModelBackends.FirstOrDefault();
            if (backend == null || backend.Provider == null) continue;
            var provider = backend.Provider;

            if (!providerCache.TryGetValue($"{provider.BaseUrl}_{provider.BearerToken}", out var physicalModels))
            {
                physicalModels = await ollamaService.GetDetailedModelsAsync(provider.BaseUrl, provider.BearerToken);
                providerCache[$"{provider.BaseUrl}_{provider.BearerToken}"] = physicalModels;
            }

            var physicalModel = physicalModels?.FirstOrDefault(m => m.Name == backend.UnderlyingModelName);
            if (physicalModel != null)
            {
                allTags.Add(new OllamaService.OllamaModel
                {
                    Name = vm.Name,
                    Model = vm.Name,
                    ModifiedAt = vm.CreatedAt,
                    Size = physicalModel.Size,
                    Digest = physicalModel.Digest,
                    Details = physicalModel.Details
                });
            }
            else
            {
                allTags.Add(new OllamaService.OllamaModel
                {
                    Name = vm.Name,
                    Model = vm.Name,
                    ModifiedAt = vm.CreatedAt,
                    Details = new OllamaService.OllamaModelDetails
                    {
                        Format = "gguf",
                        Family = vm.Type == ModelType.Chat ? "llama" : (vm.Type == ModelType.Embedding ? "bert" : "unknown"),
                        ParameterSize = "Unknown",
                        QuantizationLevel = "Unknown"
                    }
                });
            }
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(new { models = allTags }, OllamaJsonOptions),
            ContentType = "application/json",
            StatusCode = 200
        };
    }

    public async Task<IActionResult> GetPsAsync()
    {
        var virtualModels = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
            .ToListAsync();
            
        var allRunning = new List<OllamaService.OllamaRunningModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaRunningModel>?>();

        foreach (var vm in virtualModels)
        {
            var backend = vm.VirtualModelBackends.FirstOrDefault();
            if (backend == null || backend.Provider == null) continue;
            var provider = backend.Provider;

            if (!providerCache.TryGetValue($"{provider.BaseUrl}_{provider.BearerToken}", out var runningPhysical))
            {
                runningPhysical = await ollamaService.GetRunningModelsAsync(provider.BaseUrl, provider.BearerToken);
                providerCache[$"{provider.BaseUrl}_{provider.BearerToken}"] = runningPhysical;
            }

            var physicalRunning = runningPhysical?.FirstOrDefault(m => m.Name == backend.UnderlyingModelName);
            if (physicalRunning != null)
            {
                allRunning.Add(new OllamaService.OllamaRunningModel
                {
                    Name = vm.Name,
                    Model = vm.Name,
                    ModifiedAt = vm.CreatedAt,
                    Size = physicalRunning.Size,
                    Digest = physicalRunning.Digest,
                    Details = physicalRunning.Details,
                    ExpiresAt = physicalRunning.ExpiresAt,
                    SizeVram = physicalRunning.SizeVram,
                    ContextLength = physicalRunning.ContextLength
                });
            }
        }

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(new { models = allRunning }, OllamaJsonOptions),
            ContentType = "application/json",
            StatusCode = 200
        };
    }

    public async Task<IActionResult> GetVersionAsync()
    {
        var version = await globalSettingsService.GetFakeOllamaVersionAsync();
        return new ContentResult
        {
            Content = JsonSerializer.Serialize(new { version }, OllamaJsonOptions),
            ContentType = "application/json",
            StatusCode = 200
        };
    }

    public async Task<IActionResult> GetOpenAIModelsAsync()
    {
        var virtualModels = await dbContext.VirtualModels.ToListAsync();
        var data = new JsonArray();
        foreach (var vm in virtualModels)
        {
            data.Add(new JsonObject
            {
                ["id"] = vm.Name,
                ["object"] = "model",
                ["created"] = new DateTimeOffset(vm.CreatedAt).ToUnixTimeSeconds(),
                ["owned_by"] = "library"
            });
        }

        return new ContentResult
        {
            Content = new JsonObject
            {
                ["object"] = "list",
                ["data"] = data
            }.ToJsonString(),
            ContentType = "application/json",
            StatusCode = 200
        };
    }
}