using System.Collections.Concurrent;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class RateLimitService : ISingletonDependency
{
    private readonly ConcurrentDictionary<int, Queue<DateTime>> _requests = new();

    public async Task<bool> IsAllowedAsync(ApiKey apiKey)
    {
        if (!apiKey.RateLimitEnabled) return true;

        var history = _requests.GetOrAdd(apiKey.Id, _ => new Queue<DateTime>());
        var window = TimeSpan.FromSeconds(apiKey.TimeWindowSeconds);

        if (apiKey.RateLimitHang)
        {
            while (true)
            {
                TimeSpan waitTime = TimeSpan.Zero;
                lock (history)
                {
                    var now = DateTime.UtcNow;
                    while (history.TryPeek(out var time) && now - time > window)
                    {
                        history.Dequeue();
                    }

                    if (history.Count < apiKey.MaxRequests)
                    {
                        history.Enqueue(now);
                        return true;
                    }

                    if (history.TryPeek(out var oldest))
                    {
                        waitTime = window - (now - oldest);
                    }
                }

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
        }
        else
        {
            lock (history)
            {
                var now = DateTime.UtcNow;
                while (history.TryPeek(out var time) && now - time > window)
                {
                    history.Dequeue();
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
}
