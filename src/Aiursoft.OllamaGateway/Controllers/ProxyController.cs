using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway;
using Aiursoft.OllamaGateway.Gateway.Chat;
using Aiursoft.OllamaGateway.Gateway.Embeddings;
using Aiursoft.OllamaGateway.Gateway.Execution;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Aiursoft.OllamaGateway.Middlewares;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Controllers;

public class OllamaRequestModel
{
    public string Model { get; set; } = string.Empty;
    [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
    public List<OllamaMessage>? Messages { get; set; }
    public string? Prompt { get; set; }
    public bool? Stream { get; set; }
    public string? KeepAlive { get; set; }
    public OllamaRequestOptions? Options { get; set; }
    public bool? Think { get; set; }
    public string? Suffix { get; set; }
    public string? System { get; set; }
    public string? Template { get; set; }
    public string? Context { get; set; }
    public string? Format { get; set; }
    public bool? Raw { get; set; }
}

public class OllamaMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    [SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
    public List<string>? Images { get; set; }
}

// INBOUND deserialization: Newtonsoft.Json (via ASP.NET Core [FromBody] model binding).
// Newtonsoft's DefaultContractResolver matches JSON keys case-insensitively but does NOT
// treat underscores as separators — "num_ctx" will NOT bind to a plain "NumCtx" property.
// Every snake_case Ollama field therefore needs an explicit [JsonProperty] attribute.
//
// OUTBOUND serialization: System.Text.Json with SnakeCaseLower (see OllamaJsonOptions below).
// STJ ignores [Newtonsoft.Json.JsonProperty] entirely, so these attributes only affect
// the inbound path and do not alter the outbound key names.
public class OllamaRequestOptions
{
    [Newtonsoft.Json.JsonProperty("num_ctx")]
    public int? NumCtx { get; set; }

    [Newtonsoft.Json.JsonProperty("temperature")]
    public float? Temperature { get; set; }

    [Newtonsoft.Json.JsonProperty("top_p")]
    public float? TopP { get; set; }

    [Newtonsoft.Json.JsonProperty("top_k")]
    public int? TopK { get; set; }

    [Newtonsoft.Json.JsonProperty("num_predict")]
    public int? NumPredict { get; set; }

    [Newtonsoft.Json.JsonProperty("repeat_penalty")]
    public float? RepeatPenalty { get; set; }
}

[Route("api")]
[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class ProxyController(
    TemplateDbContext dbContext,
    RequestLogContext logContext,
    GlobalSettingsService globalSettingsService,
    ILogger<ProxyController> logger,
    GatewayRequestTracker requestTracker,
    IBackendInvoker backendInvoker,
    IGatewayModelResolver modelResolver,
    IEmbeddingGatewayService embeddingGatewayService,
    IChatRequestCompiler chatRequestCompiler,
    IChatResponseDispatcher chatResponseDispatcher) : ControllerBase
{
    private static readonly HashSet<string> HeaderBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Upgrade", "Host", "Accept-Ranges"
    };

    // OUTBOUND serialization to Ollama/upstream — System.Text.Json with SnakeCaseLower.
    // Used when serializing C# model objects (e.g. OllamaRequestModel) into the JSON body
    // that is forwarded to the real Ollama instance. SnakeCaseLower converts "NumCtx" → "num_ctx"
    // automatically, matching the Ollama API wire format.
    // STJ is also used throughout this controller for mutable DOM manipulation of streaming
    // NDJSON/SSE chunks (JsonNode), which has no ergonomic equivalent in Newtonsoft.
    private static readonly JsonSerializerOptions OllamaJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };


    [HttpPost("chat")]
    [EnableBodyBuffering]
    public async Task Chat([FromBody] OllamaRequestModel input)
    {
        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ??
                                    (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var resolution = await modelResolver.ResolveChatAsync(
                input.Model,
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

            Request.Body.Position = 0;
            using var bodyReader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var rawBody = await bodyReader.ReadToEndAsync(HttpContext.RequestAborted);
            var decodedRequest = chatRequestCompiler.Decode(ProtocolDialect.OllamaNative, rawBody);
            TrackChatRequest(input, virtualModel);

            var result = await backendInvoker.SendAsync(
                virtualModel,
                backend,
                decodedRequest.Request.RequiredCapabilities,
                candidate => chatRequestCompiler.CreateProviderRequest(decodedRequest, virtualModel, candidate),
                HttpContext.RequestAborted,
                decodedRequest.Request.PreferredCapabilities);

            if (result == null)
            {
                var error = $"No available backend for model '{virtualModel.Name}' supports the required request capabilities.";
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                logContext.Log.StatusCode = Response.StatusCode;
                logContext.Log.Success = false;
                logContext.Log.Answer = error;
                await Response.WriteAsync(error, HttpContext.RequestAborted);
                return;
            }

            await using (result)
            {
                var upstreamResponse = result.Response;
                logContext.Log.BackendId = result.Backend.Id;
                Response.StatusCode = (int)upstreamResponse.StatusCode;
                CopyHeaders(upstreamResponse);
                logContext.Log.StatusCode = Response.StatusCode;
                logContext.Log.Success = upstreamResponse.IsSuccessStatusCode;
                logger.LogInformation(
                    "[{TraceId}] Received response from upstream: {StatusCode} for chat request for model {Model}",
                    HttpContext.TraceIdentifier,
                    (int)upstreamResponse.StatusCode,
                    virtualModel.Name);

                if (!upstreamResponse.IsSuccessStatusCode)
                {
                    var error = await upstreamResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    logContext.Log.Answer = error;
                    await Response.WriteAsync(error, HttpContext.RequestAborted);
                    return;
                }

                await chatResponseDispatcher.WriteAsync(
                    ProtocolDialect.OllamaNative,
                    upstreamResponse,
                    virtualModel,
                    result.Backend,
                    decodedRequest.Request.Stream,
                    HttpContext);
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Chat request to Ollama was canceled by the client or timed out.");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ProxyController.Chat");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status500InternalServerError;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            requestTracker.Complete();
        }
    }

    private void TrackChatRequest(
        OllamaRequestModel input,
        VirtualModel virtualModel)
    {
        requestTracker.Begin(
            virtualModel,
            input.Messages?.LastOrDefault()?.Content ?? string.Empty,
            input.Messages?.Count ?? 0,
            User);
    }

    [HttpPost("generate")]
    public async Task Generate([FromBody] OllamaRequestModel input)
    {
        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var resolution = await modelResolver.ResolveChatAsync(
                input.Model,
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

            requestTracker.Begin(virtualModel, input.Prompt ?? string.Empty, 1, User);

            // /api/generate is an Ollama-native operation and cannot be translated
            // to either OpenAI endpoint family.
            if (BackendProtocolResolver.Resolve(backend) != BackendProtocol.OllamaNative)
            {
                Response.StatusCode = 501;
                await Response.WriteAsync("The /api/generate endpoint requires an Ollama-native backend. Use /api/chat, /v1/chat/completions, or /v1/responses instead.");
                return;
            }

            input.Model = backend.UnderlyingModelName;
            if (virtualModel.Thinking.HasValue) input.Think = virtualModel.Thinking.Value;
            input.KeepAlive ??= backend.Provider.KeepAlive;

            input.Options ??= new OllamaRequestOptions();
            if (virtualModel.NumCtx.HasValue) input.Options.NumCtx = virtualModel.NumCtx;
            if (virtualModel.Temperature.HasValue) input.Options.Temperature = virtualModel.Temperature;
            if (virtualModel.TopP.HasValue) input.Options.TopP = virtualModel.TopP;
            if (virtualModel.TopK.HasValue) input.Options.TopK = virtualModel.TopK;
            if (virtualModel.NumPredict.HasValue) input.Options.NumPredict = virtualModel.NumPredict;
            if (virtualModel.RepeatPenalty.HasValue) input.Options.RepeatPenalty = virtualModel.RepeatPenalty;

            var result = await backendInvoker.SendAsync(
                virtualModel,
                backend,
                GatewayCapability.TextGeneration,
                b =>
                {
                    input.Model = b.UnderlyingModelName;
                    return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}/api/generate")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(input, OllamaJsonOptions), Encoding.UTF8, "application/json")
                    };
                },
                HttpContext.RequestAborted);

            if (result == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                return;
            }

            await using (result)
            {
                var response = result.Response;
                backend = result.Backend;
                logContext.Log.BackendId = backend.Id;

                Response.StatusCode = (int)response.StatusCode;
                CopyHeaders(response);

                logContext.Log.StatusCode = Response.StatusCode;
                logContext.Log.Success = response.IsSuccessStatusCode;
                logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for generate request for model {Model}", HttpContext.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

                await using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                if (input.Stream != false && response.IsSuccessStatusCode)
                {
                    // Ollama native streaming: NDJSON (one JSON object per line)
                    var answerBuilder = new StringBuilder();
                    using var reader = new StreamReader(responseStream);
                    string? line;
                    while ((line = await reader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            var chunkNode = JsonNode.Parse(line);
                            if (chunkNode != null)
                            {
                                // Mask the physical model name with the virtual one
                                chunkNode["model"] = virtualModel.Name;

                                // Serialize modified JSON, prepend prefix, and send
                                var modifiedLine = chunkNode.ToJsonString();
                                await Response.WriteAsync(modifiedLine + "\n", HttpContext.RequestAborted);
                                await Response.Body.FlushAsync(HttpContext.RequestAborted);

                                // Extract audit values
                                string? contentStr = chunkNode["response"]?.ToString();
                                if (!string.IsNullOrEmpty(contentStr))
                                {
                                    answerBuilder.Append(contentStr);
                                }

                                // The final chunk (done: true) carries token counts
                                bool isDone = chunkNode["done"]?.GetValue<bool>() == true;
                                if (isDone)
                                {
                                    logContext.Log.PromptTokens = (int)(chunkNode["prompt_eval_count"]?.GetValue<long>() ?? 0);
                                    logContext.Log.CompletionTokens = (int)(chunkNode["eval_count"]?.GetValue<long>() ?? 0);
                                    logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
                                }
                                continue;
                            }
                        }
                        catch { /* Fallback to raw output on parse failure */ }

                        await Response.WriteAsync(line + "\n", HttpContext.RequestAborted);
                        await Response.Body.FlushAsync(HttpContext.RequestAborted);
                    }

                    logContext.Log.Answer = answerBuilder.ToString();
                }
                else
                {
                    using var ms = new MemoryStream();
                    await responseStream.CopyToAsync(ms, HttpContext.RequestAborted);
                    ms.Seek(0, SeekOrigin.Begin);

                    var contentReplaced = false;
                    try
                    {
                        var resultNode = await JsonNode.ParseAsync(ms, cancellationToken: HttpContext.RequestAborted);
                        if (resultNode != null)
                        {
                            // Mask model name
                            resultNode["model"] = virtualModel.Name;

                            logContext.Log.Answer = resultNode["response"]?.ToString() ?? string.Empty;
                            logContext.Log.PromptTokens = (int)(resultNode["prompt_eval_count"]?.GetValue<long>() ?? 0);
                            logContext.Log.CompletionTokens = (int)(resultNode["eval_count"]?.GetValue<long>() ?? 0);
                            logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;

                            // Write the modified JSON to the response
                            var modifiedContent = resultNode.ToJsonString();
                            await Response.WriteAsync(modifiedContent, HttpContext.RequestAborted);
                            contentReplaced = true;
                        }
                    }
                    catch { /* ignored */ }

                    if (!contentReplaced)
                    {
                        if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(logContext.Log.Answer))
                        {
                            ms.Seek(0, SeekOrigin.Begin);
                            using var sReader = new StreamReader(ms, Encoding.UTF8, false, 1024, true);
                            logContext.Log.Answer = await sReader.ReadToEndAsync(HttpContext.RequestAborted);
                        }

                        ms.Seek(0, SeekOrigin.Begin);
                        await ms.CopyToAsync(Response.Body, HttpContext.RequestAborted);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Generate request to Ollama was canceled by the client or timed out.");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ProxyController.Generate");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            requestTracker.Complete();
        }
    }

    [HttpPost("embed")]
    public async Task Embed()
    {
        JsonNode? inputNode;
        try
        {
            inputNode = await JsonNode.ParseAsync(Request.Body, cancellationToken: HttpContext.RequestAborted);
        }
        catch
        {
            inputNode = null;
        }

        if (inputNode == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Request body is empty or invalid JSON.");
            return;
        }

        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var modelName = inputNode["model"]?.GetValue<string>();
            var resolution = await modelResolver.ResolveEmbeddingAsync(
                modelName,
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

            requestTracker.Begin(
                virtualModel,
                inputNode["input"]?.ToString() ?? inputNode["prompt"]?.ToString() ?? string.Empty,
                1,
                User);

            await embeddingGatewayService.ExecuteAsync(
                ProtocolDialect.OllamaNative,
                inputNode.AsObject(),
                virtualModel,
                backend,
                HttpContext);
        }
        catch (OperationCanceledException ex)
        {
            logger.LogWarning("Embedding request to Ollama was canceled by the client or timed out.");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ProxyController.Embed");
            logContext.Log.Success = false;
            logContext.Log.Answer = ex.ToString();
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            requestTracker.Complete();
        }
    }

    private void CopyHeaders(HttpResponseMessage response)
    {
        foreach (var header in response.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
                Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in response.Content.Headers)
        {
            if (!HeaderBlacklist.Contains(header.Key))
                Response.Headers[header.Key] = header.Value.ToArray();
        }
    }

    [HttpGet("tags")]
    public async Task<IActionResult> Tags()
    {
        // Do NOT call upstream providers here. Doing so would cause infinite recursion when a
        // provider points back at this gateway (self-referential Ollama provider). The /api/tags
        // endpoint represents the gateway's own virtual models; physical metadata (size, digest)
        // is not required by Ollama clients and is omitted to keep this endpoint safe.
        var virtualModels = await dbContext.VirtualModels.ToListAsync();

        var allTags = virtualModels.Select(vm => new OllamaService.OllamaModel
        {
            Name = vm.Name,
            Model = vm.Name,
            ModifiedAt = vm.CreatedAt,
            Details = new OllamaService.OllamaModelDetails
            {
                Format = "gguf",
                Family = vm.Type == ModelType.Chat ? "llama" : "bert",
                ParameterSize = "Unknown",
                QuantizationLevel = "Unknown"
            }
        }).ToList();

        var json = JsonSerializer.Serialize(new { models = allTags }, OllamaJsonOptions);
        return Content(json, "application/json");
    }

    [HttpGet("ps")]
    public async Task<IActionResult> Ps()
    {
        // Do NOT call upstream providers here — same reason as Tags(): calling
        // GetRunningModelsAsync on a self-referential Ollama provider (localhost) would hit
        // this same endpoint and cause infinite recursion. Return all virtual models as
        // "running" with placeholder metadata; Ollama clients only need the model names.
        var virtualModels = await dbContext.VirtualModels.ToListAsync();

        var allRunning = virtualModels.Select(vm => new OllamaService.OllamaRunningModel
        {
            Name = vm.Name,
            Model = vm.Name,
            ModifiedAt = vm.CreatedAt
        }).ToList();

        var json = JsonSerializer.Serialize(new { models = allRunning }, OllamaJsonOptions);
        return Content(json, "application/json");
    }

    [HttpGet("version")]
    public async Task<IActionResult> Version()
    {
        var version = await globalSettingsService.GetFakeOllamaVersionAsync();
        var json = JsonSerializer.Serialize(new { version }, OllamaJsonOptions);
        return Content(json, "application/json");
    }
}
