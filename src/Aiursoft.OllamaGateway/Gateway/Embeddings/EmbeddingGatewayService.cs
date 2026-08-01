using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Execution;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public sealed class EmbeddingGatewayService(
    IEnumerable<IEmbeddingClientAdapter> clientAdapters,
    IEnumerable<IEmbeddingProviderAdapter> providerAdapters,
    IBackendInvoker backendInvoker,
    RequestLogContext logContext) : IEmbeddingGatewayService
{
    private static readonly HashSet<string> HeaderBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Upgrade", "Host", "Accept-Ranges"
    };

    public async Task ExecuteAsync(
        ProtocolDialect clientDialect,
        JsonObject clientBody,
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        HttpContext httpContext)
    {
        var clientAdapter = clientAdapters.Single(adapter => adapter.Dialect == clientDialect);
        var decodedRequest = clientAdapter.Decode(clientBody);

        var result = await backendInvoker.SendAsync(
            virtualModel,
            initialBackend,
            GatewayCapability.Embedding,
            backend => GetProviderAdapter(backend).CreateRequest(decodedRequest, virtualModel, backend),
            httpContext.RequestAborted);

        if (result == null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await httpContext.Response.WriteAsync(
                $"No available backend for model '{virtualModel.Name}'.",
                httpContext.RequestAborted);
            return;
        }

        await using (result)
        {
            var upstreamResponse = result.Response;
            var providerAdapter = GetProviderAdapter(result.Backend);
            logContext.Log.BackendId = result.Backend.Id;
            httpContext.Response.StatusCode = (int)upstreamResponse.StatusCode;
            logContext.Log.StatusCode = httpContext.Response.StatusCode;
            logContext.Log.Success = upstreamResponse.IsSuccessStatusCode;

            if (clientDialect == ProtocolDialect.OllamaNative &&
                providerAdapter.Dialect == ProtocolDialect.OllamaNative)
            {
                CopyHeaders(upstreamResponse, httpContext.Response);
            }

            var responseBody = await upstreamResponse.Content.ReadAsStringAsync(httpContext.RequestAborted);
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                logContext.Log.Answer = responseBody;
                if (clientDialect == ProtocolDialect.OpenAiChatCompletions)
                {
                    httpContext.Response.ContentType = "application/json";
                }

                await httpContext.Response.WriteAsync(responseBody, httpContext.RequestAborted);
                return;
            }

            try
            {
                var providerResponse = providerAdapter.DecodeResponse(responseBody);
                logContext.Log.PromptTokens = (int)providerResponse.PromptTokens;
                logContext.Log.TotalTokens = (int)providerResponse.PromptTokens;
                await clientAdapter.WriteResponseAsync(
                    providerResponse,
                    virtualModel,
                    httpContext.Response,
                    httpContext.RequestAborted);
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
            {
                await httpContext.Response.WriteAsync(responseBody, httpContext.RequestAborted);
            }
        }
    }

    private IEmbeddingProviderAdapter GetProviderAdapter(VirtualModelBackend backend)
    {
        var providerType = backend.Provider?.ProviderType
                           ?? throw new InvalidOperationException("Cannot encode an embedding request without a provider.");
        return providerAdapters.Single(adapter => adapter.ProviderType == providerType);
    }

    private static void CopyHeaders(HttpResponseMessage source, HttpResponse target)
    {
        foreach (var header in source.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
            {
                target.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in source.Content.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
            {
                target.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}
