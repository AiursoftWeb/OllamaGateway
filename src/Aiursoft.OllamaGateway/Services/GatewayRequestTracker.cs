using System.Globalization;
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
    ActiveRequestTracker activeRequestTracker,
    TrafficJournal trafficJournal)
{
    private ActiveRequestRegistration? _activeRequest;

    private TrafficEvent? _traffic;
    private bool _upstreamBodyFailed;
    private bool _answerPrefixComplete;
    private CancellationToken _aborted;

    private static string Preview(string text, out bool complete)
    {
        var end = 0;
        for (var i = 0; i < 20 && end < text.Length; i++)
            end += StringInfo.GetNextTextElementLength(text.AsSpan(end));
        // One following text element confirms that a later chunk cannot extend the twentieth.
        complete = end < text.Length;
        return text[..end];
    }

    public void ObserveAnswer(string? content)
    {
        if (_traffic == null || _answerPrefixComplete || string.IsNullOrEmpty(content)) return;
        var prefix = Preview(string.Concat(_traffic.AnswerPreview, content), out _answerPrefixComplete);
        if (_traffic.AnswerPreview == prefix) return;
        _traffic = _traffic with { AnswerPreview = prefix };
        if (_traffic.Stage == "returning") trafficJournal.Record(_traffic);
    }

    private void Move(string stage)
    {
        if (_traffic == null) return;
        _traffic = _traffic with { Stage = stage };
        trafficJournal.Record(_traffic);
    }

    public void SelectAttempt(VirtualModelBackend backend, int attempt)
    {
        if (_traffic == null) return;
        _traffic = _traffic with { Attempt = attempt, ProviderId = backend.ProviderId, BackendId = backend.Id };
        Move("selected");
    }

    public void WaitingForSlot() => Move("queued");
    public void UpstreamResponded() => Move("headers");
    public void UpstreamBodyFailed() => _upstreamBodyFailed = true;
    public void AttemptFailed() => Move("attemptFailed");

    public void Begin(
        VirtualModel virtualModel,
        string question,
        int conversationMessageCount,
        HttpContext context, string? displayQuestion = null)
    {
        if (_activeRequest != null)
            throw new InvalidOperationException("Gateway request tracking has already started.");

        var apiKeyIdClaim = context.User.FindFirst("ApiKeyId");
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
        _aborted = context.RequestAborted;
        _traffic = new TrafficEvent(_activeRequest.RequestId, virtualModel.Id, 0, null, null, "received")
        {
            QuestionPreview = Preview(displayQuestion ?? question, out _)
        };
        trafficJournal.Record(_traffic);
        context.Response.OnStarting(() =>
        {
            Move("returning");
            return Task.CompletedTask;
        });
    }

    public void BeginBackendAttempt(VirtualModelBackend backend)
    {
        var provider = backend.Provider
                       ?? throw new InvalidOperationException("Cannot track a backend attempt without a provider.");

        memoryUsageTracker.TrackUnderlyingModelUsage(provider.Id, backend.UnderlyingModelName);
        logContext.Log.BackendId = backend.Id;
        logContext.Log.ProviderId = provider.Id;
        logContext.Log.UnderlyingModelName = backend.UnderlyingModelName;
        Move("sent");
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

        Move(_aborted.IsCancellationRequested ? "cancelled" : logContext.Log.Success && !_upstreamBodyFailed ? "completed" : "failed");
        _traffic = null;
        _activeRequest = null;
        activeRequest.Complete(
            logContext.Log.Success,
            logContext.Log.Success
                ? string.Empty
                : ActiveRequestTracker.GetErrorSummary(logContext.Log.Answer),
            logContext.Log.Answer);
    }
}
