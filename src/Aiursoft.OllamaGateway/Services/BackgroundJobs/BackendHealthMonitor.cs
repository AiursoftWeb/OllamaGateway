using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class BackendHealthMonitor : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackendHealthMonitor> _logger;

    public BackendHealthMonitor(
        IServiceProvider serviceProvider,
        ILogger<BackendHealthMonitor> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckHealthAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during backend health check");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var ollamaService = scope.ServiceProvider.GetRequiredService<OllamaService>();

        var backends = await dbContext.VirtualModelBackends
            .Include(b => b.Provider)
            .Where(b => b.Enabled)
            .ToListAsync(cancellationToken);

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
                _logger.LogWarning(ex, "Health check failed for Provider ID {ProviderId}", provider.Id);
                foreach (var backend in group)
                {
                    backend.IsHealthy = false;
                    backend.IsReady = false;
                    backend.LastCheckedAt = DateTime.UtcNow;
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
