using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Gateway.Execution;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public sealed class OpenAIResponsesController(
    RequestLogContext logContext,
    ILogger<OpenAIResponsesController> logger,
    GatewayRequestTracker requestTracker,
    IBackendInvoker backendInvoker,
    IGatewayModelResolver modelResolver,
    IBackendCapabilityPlanner capabilityPlanner,
    IChatRequestCompiler chatRequestCompiler,
    IChatResponseDispatcher chatResponseDispatcher) : ControllerBase
{
    [HttpPost("/v1/responses")]
    public async Task Create()
    {
        SetLogIdentity();
        try
        {
            var body = await new StreamReader(Request.Body).ReadToEndAsync(HttpContext.RequestAborted);
            JsonObject root;
            DecodedChatRequest decoded;
            try
            {
                root = JsonNode.Parse(body)?.AsObject()
                       ?? throw new System.Text.Json.JsonException("The request body must be a JSON object.");
                decoded = chatRequestCompiler.Decode(ProtocolDialect.OpenAiResponses, body);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                await WriteErrorAsync(400, "invalid_request_error", "invalid_json", ex.Message);
                return;
            }

            if (root["previous_response_id"] != null || root["conversation"] != null)
            {
                await WriteErrorAsync(
                    400,
                    "invalid_request_error",
                    "unsupported_feature",
                    "This gateway currently supports stateless Responses requests only. Send the complete input instead of previous_response_id or conversation.");
                return;
            }
            if (ChatRequestDecoding.BoolValue(root["background"]) == true)
            {
                await WriteErrorAsync(400, "invalid_request_error", "unsupported_feature",
                    "Background Responses are not supported by this gateway.");
                return;
            }
            if (ChatRequestDecoding.BoolValue(root["store"]) == true)
            {
                await WriteErrorAsync(400, "invalid_request_error", "unsupported_feature",
                    "Stored Responses are not supported by this gateway. Use store: false.");
                return;
            }

            var requestedModel = ChatRequestDecoding.StringValue(root["model"]);
            var resolution = await modelResolver.ResolveChatAsync(
                requestedModel,
                User,
                HttpContext.RequestAborted);
            if (!resolution.IsSuccess)
            {
                await WriteErrorAsync(
                    resolution.Error!.StatusCode,
                    "invalid_request_error",
                    resolution.Error.StatusCode == 404 ? "model_not_found" : "model_unavailable",
                    resolution.Error.Message);
                return;
            }

            var virtualModel = resolution.VirtualModel!;
            var initialBackend = resolution.Backend!;
            var required = decoded.Request.RequiredCapabilities;
            var hasPersistedPool = virtualModel.VirtualModelBackends.Count > 0;
            var candidateBackends = hasPersistedPool ? virtualModel.VirtualModelBackends : [initialBackend];
            if (!candidateBackends.Any(backend =>
                    (!hasPersistedPool || (backend.Enabled && (backend.IsHealthy || backend.IsReady))) &&
                    capabilityPlanner.Supports(backend, required)))
            {
                await WriteErrorAsync(
                    400,
                    "invalid_request_error",
                    "unsupported_feature",
                    $"No backend of virtual model '{virtualModel.Name}' supports all features requested by this Responses call.");
                return;
            }

            var lastQuestion = decoded.Request.Messages
                .LastOrDefault(message => message.Role == "user")?
                .Content.OfType<GatewayTextContent>()
                .LastOrDefault()?.Text ?? string.Empty;
            requestTracker.Begin(virtualModel, lastQuestion, decoded.Request.Messages.Count, User);

            var result = await backendInvoker.SendAsync(
                virtualModel,
                initialBackend,
                required,
                candidate => chatRequestCompiler.CreateProviderRequest(decoded, virtualModel, candidate),
                HttpContext.RequestAborted,
                decoded.Request.PreferredCapabilities);
            if (result == null)
            {
                await WriteErrorAsync(503, "server_error", "backend_unavailable",
                    $"No available backend for model '{virtualModel.Name}'.");
                return;
            }

            await using (result)
            {
                logContext.Log.BackendId = result.Backend.Id;
                Response.StatusCode = (int)result.Response.StatusCode;
                logContext.Log.StatusCode = Response.StatusCode;
                logContext.Log.Success = result.Response.IsSuccessStatusCode;
                if (!result.Response.IsSuccessStatusCode)
                {
                    var upstreamError = await result.Response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    logContext.Log.Answer = upstreamError;
                    await WriteErrorAsync(
                        Response.StatusCode,
                        "server_error",
                        "upstream_error",
                        ExtractUpstreamMessage(upstreamError));
                    return;
                }

                await chatResponseDispatcher.WriteAsync(
                    ProtocolDialect.OpenAiResponses,
                    result.Response,
                    virtualModel,
                    result.Backend,
                    decoded.Request.Stream,
                    HttpContext);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Responses request was canceled by the client or timed out.");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in OpenAIResponsesController.Create");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
                await WriteErrorAsync(500, "server_error", "internal_error", "Internal Server Error in Gateway.");
        }
        finally
        {
            requestTracker.Complete();
        }
    }

    private void SetLogIdentity()
    {
        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ??
                                    (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");
    }

    private async Task WriteErrorAsync(int status, string type, string code, string message)
    {
        Response.StatusCode = status;
        Response.ContentType = "application/json";
        logContext.Log.StatusCode = status;
        logContext.Log.Success = false;
        logContext.Log.Answer = message;
        var error = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = type,
                ["param"] = null,
                ["code"] = code
            }
        };
        await Response.WriteAsync(error.ToJsonString(), HttpContext.RequestAborted);
    }

    private static string ExtractUpstreamMessage(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            return ChatRequestDecoding.StringValue(root?["error"]?["message"] ?? root?["message"], body);
        }
        catch (System.Text.Json.JsonException)
        {
            return body;
        }
    }
}
