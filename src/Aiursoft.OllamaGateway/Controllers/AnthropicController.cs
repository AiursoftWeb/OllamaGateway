using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Gateway.Execution;
using Aiursoft.OllamaGateway.Middlewares;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Models.AnthropicViewModels;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class AnthropicController : ControllerBase
{
    private readonly RequestLogContext _logContext;
    private readonly ILogger<AnthropicController> _logger;
    private readonly MemoryUsageTracker _memoryUsageTracker;
    private readonly ActiveRequestTracker _activeRequestTracker;
    private readonly IBackendInvoker _backendInvoker;
    private readonly IGatewayModelResolver _modelResolver;
    private readonly IChatRequestCompiler _chatRequestCompiler;
    private readonly IChatResponseDispatcher _chatResponseDispatcher;

    public AnthropicController(
        RequestLogContext logContext,
        ILogger<AnthropicController> logger,
        MemoryUsageTracker memoryUsageTracker,
        ActiveRequestTracker activeRequestTracker,
        IBackendInvoker backendInvoker,
        IGatewayModelResolver modelResolver,
        IChatRequestCompiler chatRequestCompiler,
        IChatResponseDispatcher chatResponseDispatcher)
    {
        _logContext = logContext;
        _logger = logger;
        _memoryUsageTracker = memoryUsageTracker;
        _activeRequestTracker = activeRequestTracker;
        _backendInvoker = backendInvoker;
        _modelResolver = modelResolver;
        _chatRequestCompiler = chatRequestCompiler;
        _chatResponseDispatcher = chatResponseDispatcher;
    }
    [HttpPost("/v1/messages")]
    [EnableBodyBuffering]
    public async Task Messages([FromBody] AnthropicMessageRequest? request)
    {
        _logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        _logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        if (request == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Invalid request body.");
            return;
        }

        VirtualModelBackend? backend = null;

        try
        {
            Request.Body.Position = 0;
            using var bodyReader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var rawBody = await bodyReader.ReadToEndAsync(HttpContext.RequestAborted);
            var decodedChatRequest = _chatRequestCompiler.Decode(ProtocolDialect.AnthropicMessages, rawBody);

            var resolution = await _modelResolver.ResolveChatAsync(
                request.Model,
                User,
                HttpContext.RequestAborted);
            if (!resolution.IsSuccess)
            {
                Response.StatusCode = resolution.Error!.StatusCode;
                await Response.WriteAsync(resolution.Error.Message, HttpContext.RequestAborted);
                return;
            }

            var virtualModel = resolution.VirtualModel!;
            backend = resolution.Backend!;
            if (backend.Provider == null) throw new InvalidOperationException("Resolved backend has no provider.");

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                _memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
                _memoryUsageTracker.TrackApiKeyModelUsage(apiKeyId, virtualModel.Name);
            }
            _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            _memoryUsageTracker.TrackVirtualModelUsage(virtualModel.Name);

            _logContext.Log.Model = virtualModel.Name;

            var conversationMessageCount = request.Messages.Count;
            _logContext.Log.ConversationMessageCount = conversationMessageCount;
            _logContext.Log.LastQuestion = request.Messages.LastOrDefault()?.Content?.ToString() ?? string.Empty;
            _activeRequestTracker.StartRequest(virtualModel.Name, _logContext.Log.LastQuestion, backend.Provider.Id, backend.UnderlyingModelName, _logContext.Log.ApiKeyName);

            var isStream = request.Stream;
            var result = await _backendInvoker.SendAsync(
                virtualModel,
                backend,
                GatewayCapability.ChatCompletion,
                b => _chatRequestCompiler.CreateProviderRequest(decodedChatRequest, virtualModel, b),
                HttpContext.RequestAborted);

            if (result == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync("No available backend.");
                return;
            }

            await using (result)
            {
                var upstreamResponse = result.Response;
                _logContext.Log.BackendId = result.Backend.Id;

                Response.StatusCode = (int)upstreamResponse.StatusCode;
                _logContext.Log.StatusCode = Response.StatusCode;
                _logContext.Log.Success = upstreamResponse.IsSuccessStatusCode;

                if (!upstreamResponse.IsSuccessStatusCode)
                {
                    var errContent = await upstreamResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    _logContext.Log.Answer = errContent;
                    Response.ContentType = "application/json";
                    await Response.WriteAsync(errContent, HttpContext.RequestAborted);
                    return;
                }

                await _chatResponseDispatcher.WriteAsync(
                    ProtocolDialect.AnthropicMessages,
                    upstreamResponse,
                    virtualModel,
                    result.Backend,
                    isStream,
                    HttpContext);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error processing Anthropic request");
            _logContext.Log.Success = false;
            Response.StatusCode = 500;
            var errObj = new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = "internal_error", ["message"] = e.Message }
            };
            await Response.WriteAsync(errObj.ToJsonString());
        }
        finally
        {
            if (!string.IsNullOrEmpty(_logContext.Log.Model))
                _activeRequestTracker.EndRequest(_logContext.Log.Model, backend?.Provider?.Id ?? 0, backend?.UnderlyingModelName ?? string.Empty, _logContext.Log.Success, _logContext.Log.Success ? string.Empty : ActiveRequestTracker.GetErrorSummary(_logContext.Log.Answer), _logContext.Log.Answer);
        }
    }
}
