using System.Collections.Concurrent;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class ProviderConcurrencyLimiter : IProviderConcurrencyLimiter, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();
    private readonly ConcurrentDictionary<int, int> _waitingCounts = new();

    public async Task<IAsyncDisposable> AcquireAsync(int providerId, int maxParallelism, CancellationToken cancellationToken)
    {
        if (maxParallelism <= 0)
            return NoOpDisposable.Instance;

        var semaphore = _semaphores.GetOrAdd(providerId, _ => new SemaphoreSlim(maxParallelism, maxParallelism));

        _waitingCounts.AddOrUpdate(providerId, 1, (_, v) => v + 1);
        try
        {
            await semaphore.WaitAsync(cancellationToken);
        }
        finally
        {
            _waitingCounts.AddOrUpdate(providerId, 0, (_, v) => Math.Max(0, v - 1));
        }

        return new SemaphoreRelease(semaphore);
    }

    public int GetWaitingCount(int providerId)
    {
        _waitingCounts.TryGetValue(providerId, out var count);
        return count;
    }

    public int GetActiveCount(int providerId)
    {
        if (!_semaphores.TryGetValue(providerId, out var semaphore))
            return 0;
        return semaphore.CurrentCount;
    }

    private sealed class SemaphoreRelease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpDisposable : IAsyncDisposable
    {
        public static readonly NoOpDisposable Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
