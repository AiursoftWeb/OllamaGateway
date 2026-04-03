using System.Collections.Concurrent;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class RateLimitService : ISingletonDependency
{
    private readonly ConcurrentDictionary<int, ConcurrentQueue<DateTime>> _requests = new();

    public async Task<bool> IsAllowedAsync(ApiKey apiKey)
    {
        if (!apiKey.RateLimitEnabled) return true;

        var history = _requests.GetOrAdd(apiKey.Id, _ => new ConcurrentQueue<DateTime>());
        var window = TimeSpan.FromSeconds(apiKey.TimeWindowSeconds);

        if (apiKey.RateLimitHang)
        {
            while (true)
            {
                var now = DateTime.UtcNow;
                while (history.TryPeek(out var time) && now - time > window)
                {
                    history.TryDequeue(out _);
                }

                if (history.Count < apiKey.MaxRequests)
                {
                    history.Enqueue(now);
                    return true;
                }

                if (history.TryPeek(out var oldest))
                {
                    var waitTime = window - (now - oldest);
                    if (waitTime > TimeSpan.Zero)
                    {
                        await Task.Delay(waitTime);
                    }
                    else
                    {
                        // Smallest wait to avoid tight loop
                        await Task.Delay(10);
                    }
                }
                else
                {
                    // Should not happen if history.Count >= MaxRequests (which is at least 1)
                    await Task.Delay(10);
                }
            }
        }
        else
        {
            var now = DateTime.UtcNow;
            while (history.TryPeek(out var time) && now - time > window)
            {
                history.TryDequeue(out _);
            }

            if (history.Count < apiKey.MaxRequests)
            {
                history.Enqueue(now);
                return true;
            }

            return false;
        }
    }
}
