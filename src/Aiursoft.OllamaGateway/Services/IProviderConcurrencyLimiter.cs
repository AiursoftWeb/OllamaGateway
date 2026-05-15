namespace Aiursoft.OllamaGateway.Services;

public interface IProviderConcurrencyLimiter
{
    Task<IAsyncDisposable> AcquireAsync(int providerId, int maxParallelism, CancellationToken cancellationToken);
}
