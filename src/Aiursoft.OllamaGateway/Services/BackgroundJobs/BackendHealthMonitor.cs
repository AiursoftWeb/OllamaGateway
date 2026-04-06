using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class BackendHealthMonitor(
    TemplateDbContext dbContext,
    OllamaService ollamaService,
    ILogger<BackendHealthMonitor> logger) : IBackgroundJob
{
    public string Name => "Backend Health Monitor";
    public string Description => "Checks the health of all AI provider backends and updates their availability status in the database.";

    public async Task ExecuteAsync()
    {
        var backends = await dbContext.VirtualModelBackends
            .Include(b => b.Provider)
            .Where(b => b.Enabled)
            .ToListAsync();

        var providerGroup = backends.GroupBy(b => b.ProviderId);

        foreach (var group in providerGroup)
        {
            var provider = group.First().Provider!;
            try
            {
                if (provider.ProviderType == ProviderType.OpenAI)
                {
                    var availableModels = await ollamaService.GetOpenAIAvailableModelsAsync(provider.BaseUrl, provider.BearerToken);
                    var availableModelNames = availableModels?.ToHashSet() ?? new HashSet<string>();

                    foreach (var backend in group)
                    {
                        backend.IsHealthy = availableModelNames.Contains(backend.UnderlyingModelName);
                        backend.IsReady = backend.IsHealthy; // OpenAI models are always "ready" if available
                        backend.LastCheckedAt = DateTime.UtcNow;
                    }
                }
                else
                {
                    var availableModels = await ollamaService.GetDetailedModelsAsync(provider.BaseUrl, provider.BearerToken);
                    var runningModels = await ollamaService.GetRunningModelsAsync(provider.BaseUrl, provider.BearerToken);

                    var availableModelNames = availableModels?.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();
                    var runningModelNames = runningModels?.Select(m => m.Name).ToHashSet() ?? new HashSet<string>();

                    foreach (var backend in group)
                    {
                        backend.IsHealthy = availableModelNames.Contains(backend.UnderlyingModelName);
                        backend.IsReady = runningModelNames.Contains(backend.UnderlyingModelName);
                        backend.LastCheckedAt = DateTime.UtcNow;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health check failed for Provider ID {ProviderId}", provider.Id);
                foreach (var backend in group)
                {
                    backend.IsHealthy = false;
                    backend.IsReady = false;
                    backend.LastCheckedAt = DateTime.UtcNow;
                }
            }
        }

        await dbContext.SaveChangesAsync();
    }
}
