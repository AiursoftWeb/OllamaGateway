using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services;

public interface IBackendInvoker
{
    /// <summary>
    /// Sends an HTTP request to the selected backend with automatic retry, circuit breaking,
    /// concurrency limiting, and per-request timeout. On success, returns a result whose
    /// disposal releases the concurrency slot and the response. Returns null when all
    /// backends are exhausted.
    /// </summary>
    Task<BackendInvocationResult?> SendAsync(
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        Func<VirtualModelBackend, HttpRequestMessage> requestFactory,
        CancellationToken clientCancellation);
}
