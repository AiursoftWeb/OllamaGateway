using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services;

public class MemoryUsageTracker(
    UsageCounter counter,
    IServiceScopeFactory scopeFactory) : ISingletonDependency
{
    public void TrackApiKeyUsage(int apiKeyId)
    {
        counter.TrackApiKeyUsage(apiKeyId);
    }

    public (DateTime? LastUsed, long TotalCalls) GetApiKeyStats(int apiKeyId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var key = db.ApiKeys.Find(apiKeyId);
        return (key?.LastUsed, key?.UsageCount ?? 0);
    }

    public IDictionary<int, (DateTime LastUsed, long TotalCalls)> GetAllApiKeyStats()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        return db.ApiKeys
            .AsNoTracking()
            .ToDictionary(k => k.Id, k => (k.LastUsed ?? DateTime.MinValue, k.UsageCount));
    }

    public void TrackUnderlyingModelUsage(int providerId, string modelName)
    {
        counter.TrackUnderlyingModelUsage(providerId, modelName);
    }

    public void TrackVirtualModelUsage(string virtualModelName)
    {
        counter.TrackVirtualModelUsage(virtualModelName);
    }

    public long GetUnderlyingModelStats(int providerId, string modelName)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var usage = db.UnderlyingModelUsages
            .AsNoTracking()
            .FirstOrDefault(u => u.ProviderId == providerId && u.ModelName == modelName);
        return usage?.UsageCount ?? 0;
    }

    public IDictionary<string, long> GetAllUnderlyingModelStats()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        return db.UnderlyingModelUsages
            .AsNoTracking()
            .ToDictionary(u => $"{u.ProviderId}_{u.ModelName}", u => u.UsageCount);
    }

    public IDictionary<string, long> GetAllVirtualModelStats()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        return db.VirtualModels
            .AsNoTracking()
            .Where(v => v.UsageCount > 0)
            .ToDictionary(v => v.Name, v => v.UsageCount);
    }
}
