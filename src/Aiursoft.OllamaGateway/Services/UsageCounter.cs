using System.Collections.Concurrent;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class UsageCounter : ISingletonDependency
{
    private ConcurrentDictionary<int, long> _apiKeyUsageBuffer = new();
    private ConcurrentDictionary<(int providerId, string modelName), long> _modelUsageBuffer = new();
    private ConcurrentDictionary<int, DateTime> _apiKeyLastUsedBuffer = new();
    private ConcurrentDictionary<(int providerId, string modelName), DateTime> _modelLastUsedBuffer = new();

    private readonly object _apiKeyLock = new();
    private readonly object _modelLock = new();

    public void TrackApiKeyUsage(int apiKeyId)
    {
        _apiKeyUsageBuffer.AddOrUpdate(apiKeyId, 1, (_, current) => current + 1);
        _apiKeyLastUsedBuffer.AddOrUpdate(apiKeyId, DateTime.UtcNow, (_, _) => DateTime.UtcNow);
    }

    public void TrackUnderlyingModelUsage(int providerId, string modelName)
    {
        _modelUsageBuffer.AddOrUpdate((providerId, modelName), 1, (_, current) => current + 1);
        _modelLastUsedBuffer.AddOrUpdate((providerId, modelName), DateTime.UtcNow, (_, _) => DateTime.UtcNow);
    }

    public (IDictionary<int, long> usages, IDictionary<int, DateTime> lastUsed) SwapApiKeyBuffers()
    {
        lock (_apiKeyLock)
        {
            var oldUsages = _apiKeyUsageBuffer;
            var oldLastUsed = _apiKeyLastUsedBuffer;
            _apiKeyUsageBuffer = new();
            _apiKeyLastUsedBuffer = new();
            return (oldUsages, oldLastUsed);
        }
    }

    public (IDictionary<(int providerId, string modelName), long> usages, IDictionary<(int providerId, string modelName), DateTime> lastUsed) SwapModelBuffers()
    {
        lock (_modelLock)
        {
            var oldUsages = _modelUsageBuffer;
            var oldLastUsed = _modelLastUsedBuffer;
            _modelUsageBuffer = new();
            _modelLastUsedBuffer = new();
            return (oldUsages, oldLastUsed);
        }
    }
}
