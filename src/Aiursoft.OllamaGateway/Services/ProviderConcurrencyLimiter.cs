using System.Collections.Concurrent;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class ProviderConcurrencyLimiter : IProviderConcurrencyLimiter, ISingletonDependency
{
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _semaphores = new();

    public async Task<IAsyncDisposable> AcquireAsync(int providerId, int maxParallelism, CancellationToken cancellationToken)
    {
        if (maxParallelism <= 0)
            return NoOpDisposable.Instance;

        var semaphore = _semaphores.GetOrAdd(providerId, _ => new SemaphoreSlim(maxParallelism, maxParallelism));
        await semaphore.WaitAsync(cancellationToken);
        return new SemaphoreRelease(semaphore);
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
