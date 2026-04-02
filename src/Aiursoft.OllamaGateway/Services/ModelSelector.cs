using System.Collections.Concurrent;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class ModelSelector : IModelSelector, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, int> _roundRobinStates = new();
    private readonly ConcurrentDictionary<int, (int FailureCount, DateTime? BanUntil)> _circuitBreakerStates = new();

    public VirtualModelBackend? SelectBackend(VirtualModel virtualModel)
    {
        var backends = virtualModel.VirtualModelBackends
            .Where(b => b.Enabled && (b.IsHealthy || b.IsReady))
            .Where(b => !_circuitBreakerStates.TryGetValue(b.Id, out var state) || state.BanUntil == null || state.BanUntil < DateTime.UtcNow)
            .ToList();

        if (!backends.Any())
        {
            return null;
        }

        return virtualModel.SelectionStrategy switch
        {
            SelectionStrategy.PriorityFallback => backends.OrderBy(b => b.Priority).First(),
            SelectionStrategy.WeightedRandom => GetWeightedRandom(backends),
            SelectionStrategy.RoundRobin => GetRoundRobin(virtualModel.Id, backends),
            _ => backends.OrderBy(b => b.Priority).First()
        };
    }

    public void ReportSuccess(int backendId)
    {
        _circuitBreakerStates.TryRemove(backendId, out _);
    }

    public void ReportFailure(int backendId)
    {
        _circuitBreakerStates.AddOrUpdate(
            backendId,
            _ => (1, null),
            (_, current) =>
            {
                var newCount = current.FailureCount + 1;
                DateTime? banUntil = current.BanUntil;

                if (newCount >= 3)
                {
                    // Stepped exponential backoff: 5^(n-3) minutes
                    // n=3 -> 1 min, n=4 -> 5 min, n=5 -> 25 min, etc.
                    var exponent = newCount - 3;
                    var minutesToBan = (int)Math.Pow(5, exponent);

                    // Cap the ban time at 24 hours (1440 minutes)
                    minutesToBan = Math.Min(minutesToBan, 1440);

                    banUntil = DateTime.UtcNow.AddMinutes(minutesToBan);
                }

                return (newCount, banUntil);
            }
        );
    }

    public DateTime? GetBanUntil(int backendId)
    {
        if (_circuitBreakerStates.TryGetValue(backendId, out var state))
        {
            if (state.BanUntil > DateTime.UtcNow)
            {
                return state.BanUntil;
            }
        }
        return null;
    }

    private VirtualModelBackend GetWeightedRandom(List<VirtualModelBackend> backends)
    {
        var totalWeight = backends.Sum(b => b.Weight);
        if (totalWeight <= 0) return backends.First();

        var random = Random.Shared.Next(0, totalWeight);
        var current = 0;
        foreach (var backend in backends)
        {
            current += backend.Weight;
            if (random < current)
            {
                return backend;
            }
        }

        return backends.First();
    }

    private VirtualModelBackend GetRoundRobin(int virtualModelId, List<VirtualModelBackend> backends)
    {
        var index = _roundRobinStates.AddOrUpdate(
            virtualModelId,
            _ => 0,
            (_, current) => (current + 1) % backends.Count
        );

        if (index >= backends.Count)
        {
             index = 0;
             _roundRobinStates[virtualModelId] = 0;
        }

        return backends[index];
    }
}
