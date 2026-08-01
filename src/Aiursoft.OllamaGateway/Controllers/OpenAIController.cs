using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Gateway.Embeddings;
using Aiursoft.OllamaGateway.Gateway.Execution;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class OpenAIController : ControllerBase
{
    private readonly TemplateDbContext _dbContext;
    private readonly RequestLogContext _logContext;
    private readonly ILogger<OpenAIController> _logger;
    private readonly GatewayRequestTracker _requestTracker;
    private readonly IBackendInvoker _backendInvoker;
    private readonly IGatewayModelResolver _modelResolver;
    private readonly IEmbeddingGatewayService _embeddingGatewayService;
    private readonly IChatRequestCompiler _chatRequestCompiler;
    private readonly IChatResponseDispatcher _chatResponseDispatcher;

    public OpenAIController(
        TemplateDbContext dbContext,
        RequestLogContext logContext,
        ILogger<OpenAIController> logger,
        GatewayRequestTracker requestTracker,
        IBackendInvoker backendInvoker,
        IGatewayModelResolver modelResolver,
        IEmbeddingGatewayService embeddingGatewayService,
        IChatRequestCompiler chatRequestCompiler,
        IChatResponseDispatcher chatResponseDispatcher)
    {
        _dbContext = dbContext;
        _logContext = logContext;
        _logger = logger;
        _requestTracker = requestTracker;
        _backendInvoker = backendInvoker;
        _modelResolver = modelResolver;
        _embeddingGatewayService = embeddingGatewayService;
        _chatRequestCompiler = chatRequestCompiler;
        _chatResponseDispatcher = chatResponseDispatcher;
    }

    [HttpPost("/v1/chat/completions")]
    public async Task Chat()
    {
        _logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        _logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ??
                                     (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var body = await new StreamReader(Request.Body).ReadToEndAsync();
            var clientJson = JsonNode.Parse(body)?.AsObject();
            if (clientJson == null)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                await Response.WriteAsync("Invalid JSON body.");
                return;
            }

            var decodedRequest = _chatRequestCompiler.Decode(
                ProtocolDialect.OpenAiChatCompletions,
                body);
            var requestedModel = clientJson["model"]?.ToString() ?? string.Empty;
            var resolution = await _modelResolver.ResolveChatAsync(
                requestedModel,
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
            if (backend.Provider == null)
                throw new InvalidOperationException("Resolved backend has no provider.");

            TrackRequest(clientJson, virtualModel);
            var streaming = decodedRequest.Request.Stream;
            var result = await _backendInvoker.SendAsync(
                virtualModel,
                backend,
                GatewayCapability.ChatCompletion,
                candidate => _chatRequestCompiler.CreateProviderRequest(decodedRequest, virtualModel, candidate),
                HttpContext.RequestAborted);

            if (result == null)
            {
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await Response.WriteAsync(
                    $"No available backend for model '{virtualModel.Name}'.",
                    HttpContext.RequestAborted);
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
                    var error = await upstreamResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    _logContext.Log.Answer = error;
                    Response.ContentType = "application/json";
                    await Response.WriteAsync(error, HttpContext.RequestAborted);
                    return;
                }

                await _chatResponseDispatcher.WriteAsync(
                    ProtocolDialect.OpenAiChatCompletions,
                    upstreamResponse,
                    virtualModel,
                    result.Backend,
                    streaming,
                    HttpContext);
            }
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("OpenAI chat request was canceled by the client or timed out.");
            _logContext.Log.Success = false;
            _logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OpenAIController.Chat");
            _logContext.Log.Success = false;
            _logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            _requestTracker.Complete();
        }
    }

    private void TrackRequest(
        JsonObject clientJson,
        VirtualModel virtualModel)
    {
        var messages = clientJson["messages"]?.AsArray();
        var lastQuestion = messages?.LastOrDefault()?["content"]?.ToString() ?? string.Empty;
        _requestTracker.Begin(virtualModel, lastQuestion, messages?.Count ?? 0, User);
    }

[HttpPost("/v1/embeddings")]
    public async Task Embed()
    {
        _logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        _logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var bodyStr = await new StreamReader(Request.Body).ReadToEndAsync();
            var clientJson = JsonNode.Parse(bodyStr)?.AsObject();
            if (clientJson == null)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Invalid JSON body.");
                return;
            }

            var inputModelVal = clientJson["model"]?.ToString() ?? string.Empty;
            var resolution = await _modelResolver.ResolveEmbeddingAsync(
                inputModelVal,
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

            _requestTracker.Begin(
                virtualModel,
                clientJson["input"]?.ToString() ?? string.Empty,
                1,
                User);

            await _embeddingGatewayService.ExecuteAsync(
                ProtocolDialect.OpenAiChatCompletions,
                clientJson,
                virtualModel,
                backend,
                HttpContext);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning("OpenAI embedding request was canceled by the client or timed out.");
            _logContext.Log.Success = false;
            _logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OpenAIController.Embed");
            _logContext.Log.Success = false;
            _logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            _requestTracker.Complete();
        }
    }

    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models()
    {
        var virtualModels = await _dbContext.VirtualModels.ToListAsync();

        var data = virtualModels.Select(vm => new
        {
            id = vm.Name,
            @object = "model",
            created = ((DateTimeOffset)vm.CreatedAt).ToUnixTimeSeconds(),
            owned_by = "library"
        }).ToList();

        var json = System.Text.Json.JsonSerializer.Serialize(new { @object = "list", data }, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });
        return Content(json, "application/json");
    }

    [HttpGet("/v1/models/{id}")]
    public async Task<IActionResult> Model(string id)
    {
        var vm = await _dbContext.VirtualModels.FirstOrDefaultAsync(m => m.Name == id);
        if (vm == null)
        {
            return NotFound(new { error = new { message = $"Model '{id}' not found in gateway.", type = "invalid_request_error", param = "model", code = "model_not_found" } });
        }

        var result = new
        {
            id = vm.Name,
            @object = "model",
            created = ((DateTimeOffset)vm.CreatedAt).ToUnixTimeSeconds(),
            owned_by = "library"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });
        return Content(json, "application/json");
    }

}
