using System.Security.Claims;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy;

public class ProxyTelemetryService(
    RequestLogContext logContext,
    MemoryUsageTracker memoryUsageTracker) : IScopedDependency
{
    public void TrackRequest(ClaimsPrincipal user, string modelName, string? lastQuestion, int conversationMessageCount)
    {
        logContext.Log.UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = user.FindFirst("ApiKeyName")?.Value ?? (user.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        var apiKeyIdClaim = user.FindFirst("ApiKeyId");
        if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
        {
            memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
        }
        
        logContext.Log.Model = modelName;
        logContext.Log.ConversationMessageCount = conversationMessageCount;
        logContext.Log.LastQuestion = lastQuestion ?? string.Empty;
    }

    public void TrackUpstreamResponse(int statusCode, bool isSuccess)
    {
        logContext.Log.StatusCode = statusCode;
        logContext.Log.Success = isSuccess;
    }
}
