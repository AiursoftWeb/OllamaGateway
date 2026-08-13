using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
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
    private readonly GatewayRequestTracker _requestTracker;
    private readonly IBackendInvoker _backendInvoker;
    private readonly IGatewayModelResolver _modelResolver;
    private readonly IChatRequestCompiler _chatRequestCompiler;
    private readonly IChatResponseDispatcher _chatResponseDispatcher;

    public AnthropicController(
        RequestLogContext logContext,
        ILogger<AnthropicController> logger,
        GatewayRequestTracker requestTracker,
        IBackendInvoker backendInvoker,
        IGatewayModelResolver modelResolver,
        IChatRequestCompiler chatRequestCompiler,
        IChatResponseDispatcher chatResponseDispatcher)
    {
        _logContext = logContext;
        _logger = logger;
        _requestTracker = requestTracker;
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
            var backend = resolution.Backend!;
            if (backend.Provider == null) throw new InvalidOperationException("Resolved backend has no provider.");

            var conversationMessageCount = request.Messages.Count;
            _requestTracker.Begin(
                virtualModel,
                request.Messages.LastOrDefault()?.Content?.ToString() ?? string.Empty,
                conversationMessageCount,
                User);

            var isStream = request.Stream;
            var result = await _backendInvoker.SendAsync(
                virtualModel,
                backend,
                decodedChatRequest.Request.RequiredCapabilities,
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
            _requestTracker.Complete();
        }
    }
}
