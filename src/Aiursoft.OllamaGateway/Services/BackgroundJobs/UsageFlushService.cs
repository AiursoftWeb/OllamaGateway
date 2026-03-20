using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class UsageFlushService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<UsageFlushService> _logger;
    private readonly UsageCounter _usageCounter;

    public UsageFlushService(
        IServiceProvider serviceProvider,
        ILogger<UsageFlushService> logger,
        UsageCounter usageCounter)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _usageCounter = usageCounter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Usage Flush Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
                await FlushUsageAsync();
            }
            catch (TaskCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while flushing usage.");
            }
        }

        // Final flush on shutdown
        try
        {
            await FlushUsageAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Final usage flush failed.");
        }
        _logger.LogInformation("Usage Flush Service is stopping.");
    }

    private async Task FlushUsageAsync()
    {
        _logger.LogInformation("Flushing usage stats to database...");
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var isInMemory = dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";

        // Flush API Keys
        var (apiKeyUsages, apiKeyLastUsed) = _usageCounter.SwapApiKeyBuffers();
        foreach (var apiKeyId in apiKeyUsages.Keys)
        {
            var count = apiKeyUsages[apiKeyId];
            var lastUsed = apiKeyLastUsed[apiKeyId];
            
            if (isInMemory)
            {
                var apiKey = await dbContext.ApiKeys.FindAsync(apiKeyId);
                if (apiKey != null)
                {
                    apiKey.UsageCount += count;
                    apiKey.LastUsed = lastUsed;
                }
            }
            else
            {
                await dbContext.ApiKeys
                    .Where(a => a.Id == apiKeyId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.UsageCount, a => a.UsageCount + count)
                        .SetProperty(a => a.LastUsed, lastUsed));
            }
        }
        
        if (isInMemory)
        {
            await dbContext.SaveChangesAsync();
        }

        // Flush Models
        var (modelUsages, modelLastUsed) = _usageCounter.SwapModelBuffers();
        foreach (var modelKey in modelUsages.Keys)
        {
            var count = modelUsages[modelKey];
            var lastUsed = modelLastUsed[modelKey];

            var existing = await dbContext.UnderlyingModelUsages
                .FirstOrDefaultAsync(u => u.ProviderId == modelKey.providerId && u.ModelName == modelKey.modelName);

            if (existing != null)
            {
                if (isInMemory)
                {
                    existing.UsageCount += count;
                    existing.LastUsed = lastUsed;
                }
                else
                {
                    await dbContext.UnderlyingModelUsages
                        .Where(u => u.Id == existing.Id)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(u => u.UsageCount, u => u.UsageCount + count)
                            .SetProperty(u => u.LastUsed, lastUsed));
                }
            }
            else
            {
                try
                {
                    dbContext.UnderlyingModelUsages.Add(new UnderlyingModelUsage
                    {
                        ProviderId = modelKey.providerId,
                        ModelName = modelKey.modelName,
                        UsageCount = count,
                        LastUsed = lastUsed
                    });
                    await dbContext.SaveChangesAsync();
                }
                catch (Exception) // Could be DbUpdateException or similar
                {
                    // Someone else might have inserted it
                    if (isInMemory)
                    {
                        var again = await dbContext.UnderlyingModelUsages
                            .FirstOrDefaultAsync(u => u.ProviderId == modelKey.providerId && u.ModelName == modelKey.modelName);
                        if (again != null)
                        {
                            again.UsageCount += count;
                            again.LastUsed = lastUsed;
                            await dbContext.SaveChangesAsync();
                        }
                    }
                    else
                    {
                        await dbContext.UnderlyingModelUsages
                            .Where(u => u.ProviderId == modelKey.providerId && u.ModelName == modelKey.modelName)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(u => u.UsageCount, u => u.UsageCount + count)
                                .SetProperty(u => u.LastUsed, lastUsed));
                    }
                }
            }
        }
        
        if (isInMemory)
        {
            await dbContext.SaveChangesAsync();
        }
    }
}
