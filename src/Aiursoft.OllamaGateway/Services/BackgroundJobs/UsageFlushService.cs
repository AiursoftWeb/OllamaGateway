using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class UsageFlushService(
    TemplateDbContext dbContext,
    UsageCounter usageCounter,
    ILogger<UsageFlushService> logger) : IBackgroundJob
{
    public string Name => "Usage Flush";
    public string Description => "Flushes in-memory API usage counters to the database to keep usage statistics up to date.";

    public async Task ExecuteAsync()
    {
        logger.LogInformation("Flushing usage stats to database...");
        var isInMemory = dbContext.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";

        // Flush API Keys
        var (apiKeyUsages, apiKeyLastUsed) = usageCounter.SwapApiKeyBuffers();
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
        var (modelUsages, modelLastUsed) = usageCounter.SwapModelBuffers();
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

        // Flush Virtual Models
        var virtualModelUsages = usageCounter.SwapVirtualModelBuffers();
        foreach (var modelName in virtualModelUsages.Keys)
        {
            var count = virtualModelUsages[modelName];
            if (isInMemory)
            {
                var model = await dbContext.VirtualModels.FirstOrDefaultAsync(v => v.Name == modelName);
                if (model != null)
                {
                    model.UsageCount += count;
                }
            }
            else
            {
                await dbContext.VirtualModels
                    .Where(v => v.Name == modelName)
                    .ExecuteUpdateAsync(s => s.SetProperty(v => v.UsageCount, v => v.UsageCount + count));
            }
        }

        if (isInMemory)
        {
            await dbContext.SaveChangesAsync();
        }
    }
}
