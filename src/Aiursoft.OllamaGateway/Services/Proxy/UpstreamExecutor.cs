using System.Text;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Services.Proxy;

public class UpstreamExecutor(
    IHttpClientFactory httpClientFactory,
    GlobalSettingsService globalSettingsService,
    IModelSelector modelSelector,
    ILogger<UpstreamExecutor> logger,
    MemoryUsageTracker memoryUsageTracker,
    RequestLogContext logContext) : IUpstreamExecutor
{
    public async Task<(HttpResponseMessage Response, VirtualModelBackend FinalBackend)> ExecuteWithRetriesAsync(
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        string endpoint,
        string requestJson,
        HttpContext context)
    {
        var backend = initialBackend;
        for (var i = 0; i < virtualModel.MaxRetries; i++)
        {
            if (backend.Provider == null)
            {
                throw new NoAvailableBackendException(virtualModel.Name);
            }

            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            
            var client = httpClientFactory.CreateClient();
            client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
            if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
            }

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}{endpoint}")
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

            logger.LogInformation("[{TraceId}] Proxying request for model {Model} to {UnderlyingUrl}, attempt {Attempt}", context.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);

            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    modelSelector.ReportSuccess(backend.Id);
                    logContext.Log.BackendId = backend.Id;
                    return (response, backend);
                }

                if ((int)response.StatusCode >= 500 && !context.Response.HasStarted)
                {
                    throw new HttpRequestException($"Received {response.StatusCode} from upstream.");
                }

                modelSelector.ReportSuccess(backend.Id);
                logContext.Log.BackendId = backend.Id;
                return (response, backend);
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                modelSelector.ReportFailure(backend.Id);
                logger.LogWarning(ex, "Attempt {Attempt} failed for model {Model} to {UnderlyingUrl}", i + 1, virtualModel.Name, underlyingUrl);
                
                if (i == virtualModel.MaxRetries - 1)
                {
                    throw;
                }

                var nextBackend = modelSelector.SelectBackend(virtualModel);
                if (nextBackend == null || nextBackend.Provider == null)
                {
                    throw new NoAvailableBackendException(virtualModel.Name);
                }
                backend = nextBackend;
            }
        }

        throw new NoAvailableBackendException(virtualModel.Name);
    }
}
