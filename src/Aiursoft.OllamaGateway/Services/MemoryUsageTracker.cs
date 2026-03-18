using System.Collections.Concurrent;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class MemoryUsageTracker : ISingletonDependency
{
    // apiKeyId -> (LastUsed, TotalCalls)
    private readonly ConcurrentDictionary<int, (DateTime LastUsed, long TotalCalls)> _apiKeyStats = new();

    // providerId_modelName -> TotalCalls
    private readonly ConcurrentDictionary<string, long> _underlyingModelStats = new();

    public void TrackApiKeyUsage(int apiKeyId)
    {
        _apiKeyStats.AddOrUpdate(
            apiKeyId,
            _ => (DateTime.UtcNow, 1),
            (_, current) => (DateTime.UtcNow, current.TotalCalls + 1)
        );
    }

    public (DateTime? LastUsed, long TotalCalls) GetApiKeyStats(int apiKeyId)
    {
        if (_apiKeyStats.TryGetValue(apiKeyId, out var stats))
        {
            return (stats.LastUsed, stats.TotalCalls);
        }
        return (null, 0);
    }

    public void TrackUnderlyingModelUsage(int providerId, string modelName)
    {
        var key = $"{providerId}_{modelName}";
        _underlyingModelStats.AddOrUpdate(
            key,
            _ => 1,
            (_, currentCalls) => currentCalls + 1
        );
    }

    public long GetUnderlyingModelStats(int providerId, string modelName)
    {
        var key = $"{providerId}_{modelName}";
        if (_underlyingModelStats.TryGetValue(key, out var stats))
        {
            return stats;
        }
        return 0;
    }
}
