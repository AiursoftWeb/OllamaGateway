using System.Security.Claims;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Services;

/// <summary>
/// Coordinates request-level accounting with the physical backend attempts selected by
/// <see cref="IBackendInvoker"/>. This service is scoped to one HTTP request.
/// </summary>
public sealed class GatewayRequestTracker(
    RequestLogContext logContext,
    MemoryUsageTracker memoryUsageTracker,
    ActiveRequestTracker activeRequestTracker)
{
    private ActiveRequestRegistration? _activeRequest;

    public void Begin(
        VirtualModel virtualModel,
        string question,
        int conversationMessageCount,
        ClaimsPrincipal user)
    {
        if (_activeRequest != null)
            throw new InvalidOperationException("Gateway request tracking has already started.");

        var apiKeyIdClaim = user.FindFirst("ApiKeyId");
        if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
        {
            memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            memoryUsageTracker.TrackApiKeyModelUsage(apiKeyId, virtualModel.Name);
        }

        memoryUsageTracker.TrackVirtualModelUsage(virtualModel.Name);
        logContext.Log.Model = virtualModel.Name;
        logContext.Log.ConversationMessageCount = conversationMessageCount;
        logContext.Log.LastQuestion = question;
        _activeRequest = activeRequestTracker.BeginRequest(
            virtualModel.Name,
            question,
            logContext.Log.ApiKeyName);
    }

    public void BeginBackendAttempt(VirtualModelBackend backend)
    {
        var provider = backend.Provider
                       ?? throw new InvalidOperationException("Cannot track a backend attempt without a provider.");

        memoryUsageTracker.TrackUnderlyingModelUsage(provider.Id, backend.UnderlyingModelName);
        logContext.Log.BackendId = backend.Id;
        logContext.Log.ProviderId = provider.Id;
        logContext.Log.UnderlyingModelName = backend.UnderlyingModelName;
        _activeRequest?.SetBackend(provider.Id, backend.UnderlyingModelName);
    }

    public void EndBackendAttempt()
    {
        _activeRequest?.ClearBackend();
    }

    public void Complete()
    {
        var activeRequest = _activeRequest;
        if (activeRequest == null)
            return;

        _activeRequest = null;
        activeRequest.Complete(
            logContext.Log.Success,
            logContext.Log.Success
                ? string.Empty
                : ActiveRequestTracker.GetErrorSummary(logContext.Log.Answer),
            logContext.Log.Answer);
    }
}
