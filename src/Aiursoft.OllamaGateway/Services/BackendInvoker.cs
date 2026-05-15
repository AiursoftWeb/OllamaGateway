using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services;

public class BackendInvoker(
    IHttpClientFactory httpClientFactory,
    GlobalSettingsService globalSettingsService,
    IModelSelector modelSelector,
    IProviderConcurrencyLimiter concurrencyLimiter,
    MemoryUsageTracker memoryUsageTracker,
    ILogger<BackendInvoker> logger) : IBackendInvoker, ISingletonDependency
{
    public async Task<BackendInvocationResult?> SendAsync(
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        Func<VirtualModelBackend, HttpRequestMessage> requestFactory,
        CancellationToken clientCancellation)
    {
        var backend = initialBackend;
        IAsyncDisposable? concurrencySlot = null;

        for (var i = 0; i < virtualModel.MaxRetries; i++)
        {
            if (backend?.Provider == null)
                break;

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(clientCancellation);
            cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

            // Queue for a concurrency slot — this wait does NOT count toward HealthCheckTimeout
            try
            {
                concurrencySlot = await concurrencyLimiter.AcquireAsync(
                    backend.Provider.Id, backend.Provider.MaxParallelism, clientCancellation);
            }
            catch (OperationCanceledException)
            {
                concurrencySlot = null;
                if (i == virtualModel.MaxRetries - 1) break;
                backend = modelSelector.SelectBackend(virtualModel);
                continue;
            }

            try
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
                if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
                }

                var request = requestFactory(backend);

                logger.LogInformation("Backend request to {Url}, attempt {Attempt}", underlyingUrl, i + 1);

                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    modelSelector.ReportSuccess(backend.Id);
                    return new BackendInvocationResult(response, backend, concurrencySlot);
                }
                if ((int)response.StatusCode >= 500)
                {
                    modelSelector.ReportFailure(backend.Id);
                    logger.LogWarning("Backend request attempt {Attempt} returned {StatusCode}", i + 1, (int)response.StatusCode);
                    if (i == virtualModel.MaxRetries - 1)
                        return new BackendInvocationResult(response, backend, concurrencySlot);
                    await concurrencySlot.DisposeAsync();
                    concurrencySlot = null;
                    response.Dispose();
                    backend = modelSelector.SelectBackend(virtualModel);
                    if (backend?.Provider == null) break;
                    memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                    continue;
                }
                // 4xx or other non-5xx: treat as success, don't retry
                modelSelector.ReportSuccess(backend.Id);
                return new BackendInvocationResult(response, backend, concurrencySlot);
            }
            catch (OperationCanceledException) when (clientCancellation.IsCancellationRequested)
            {
                // Client disconnected — release the concurrency slot but do NOT report
                // a failure to the circuit breaker (the backend is not at fault).
                if (concurrencySlot != null)
                {
                    await concurrencySlot.DisposeAsync();
                }
                throw;
            }
            catch (Exception ex)
            {
                if (concurrencySlot != null)
                {
                    await concurrencySlot.DisposeAsync();
                    concurrencySlot = null;
                }
                modelSelector.ReportFailure(backend!.Id);
                logger.LogWarning(ex, "Backend request attempt {Attempt} failed", i + 1);

                if (i == virtualModel.MaxRetries - 1)
                    break;

                backend = modelSelector.SelectBackend(virtualModel);
                if (backend?.Provider == null)
                    break;

                memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            }
        }

        if (concurrencySlot != null)
            await concurrencySlot.DisposeAsync();

        return null;
    }
}
