using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
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
    MemoryUsageTracker memoryUsageTracker,
    ActiveRequestTracker activeRequestTracker,
    IModelSelector modelSelector,
    IBackendInvoker backendInvoker) : ControllerBase
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
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var modelToUse = string.IsNullOrWhiteSpace(input.Model)
                ? await globalSettingsService.GetDefaultChatModelAsync()
                : input.Model;

            VirtualModel? virtualModel = null;
            VirtualModelBackend? backend = null;

            if (modelToUse.StartsWith("physical_"))
            {
                var parts = modelToUse.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out var providerId))
                {
                    if (!User.HasClaim(AppPermissions.Type, AppPermissionNames.CanChatWithUnderlyingModels))
                    {
                        Response.StatusCode = 403;
                        await Response.WriteAsync("Forbidden. You don't have permission to chat with underlying models.");
                        return;
                    }

                    var provider = await dbContext.OllamaProviders.FindAsync(providerId);
                    if (provider == null)
                    {
                        Response.StatusCode = 404;
                        await Response.WriteAsync($"Provider with ID {providerId} not found.");
                        return;
                    }

                    var underlyingModelName = string.Join('_', parts.Skip(2));
                    virtualModel = new VirtualModel
                    {
                        Name = modelToUse,
                        MaxRetries = 1,
                        HealthCheckTimeout = 40,
                    };
                    backend = new VirtualModelBackend
                    {
                        Provider = provider,
                        UnderlyingModelName = underlyingModelName,
                        ProviderId = providerId
                    };
                }
            }

            if (virtualModel == null)
            {
                virtualModel = await dbContext.VirtualModels
                    .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                    .FirstOrDefaultAsync(m => m.Name == modelToUse && m.Type == ModelType.Chat);

                if (virtualModel == null)
                {
                    Response.StatusCode = 404;
                    await Response.WriteAsync($"Model '{modelToUse}' not found in gateway.");
                    return;
                }

                backend = modelSelector.SelectBackend(virtualModel);
            }

            if (backend == null || backend.Provider == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync($"No available backend for model '{modelToUse}'.");
                return;
            }

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
                memoryUsageTracker.TrackApiKeyModelUsage(apiKeyId, virtualModel.Name);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            memoryUsageTracker.TrackVirtualModelUsage(virtualModel.Name);

            logContext.Log.Model = virtualModel.Name;
            logContext.Log.ConversationMessageCount = input.Messages?.Count ?? 0;
            logContext.Log.LastQuestion = input.Messages?.LastOrDefault()?.Content ?? string.Empty;
            logContext.Log.ProviderId = backend.Provider.Id;
            logContext.Log.UnderlyingModelName = backend.UnderlyingModelName;
            activeRequestTracker.StartRequest(virtualModel.Name, logContext.Log.LastQuestion, backend.Provider.Id, backend.UnderlyingModelName, logContext.Log.ApiKeyName);

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

            // ====================================================================
            // OpenAI-compatible Backend Path (Ollama request → OpenAI downstream)
            // ====================================================================
            if (backend.Provider.ProviderType == ProviderType.OpenAI)
            {
                var openAiReqBody = new JsonObject
                {
                    ["model"] = backend.UnderlyingModelName,
                    ["stream"] = input.Stream ?? false
                };

                if (input.Stream == true)
                    openAiReqBody["stream_options"] = new JsonObject { ["include_usage"] = true };

                if (input.Messages != null)
                {
                    var msgs = new JsonArray();
                    foreach (var msg in input.Messages)
                    {
                        var newMsg = new JsonObject { ["role"] = msg.Role };
                        if (msg.Images is { Count: > 0 })
                        {
                            var contentParts = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["type"] = "text",
                                    ["text"] = msg.Content
                                }
                            };
                            foreach (var img in msg.Images)
                            {
                                contentParts.Add(new JsonObject
                                {
                                    ["type"] = "image_url",
                                    ["image_url"] = new JsonObject
                                    {
                                        ["url"] = $"data:image/png;base64,{img}"
                                    }
                                });
                            }
                            newMsg["content"] = contentParts;
                        }
                        else
                        {
                            newMsg["content"] = msg.Content;
                        }
                        msgs.Add(newMsg);
                    }
                    openAiReqBody["messages"] = msgs;
                }

                // Map supported Ollama options → OpenAI params; silently drop top_k / num_ctx / repeat_penalty
                if (input.Options != null)
                {
                    if (input.Options.Temperature.HasValue) openAiReqBody["temperature"] = input.Options.Temperature.Value;
                    if (input.Options.TopP.HasValue) openAiReqBody["top_p"] = input.Options.TopP.Value;
                    if (input.Options.NumPredict.HasValue) openAiReqBody["max_tokens"] = input.Options.NumPredict.Value;
                }

                if (input.Think.HasValue)
                    openAiReqBody["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = input.Think.Value };

                var oaiResult = await backendInvoker.SendAsync(
                    virtualModel,
                    backend,
                    b =>
                    {
                        openAiReqBody["model"] = b.UnderlyingModelName;
                        return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}/v1/chat/completions")
                        {
                            Content = new StringContent(openAiReqBody.ToJsonString(), Encoding.UTF8, "application/json")
                        };
                    },
                    HttpContext.RequestAborted);

                if (oaiResult == null)
                {
                    Response.StatusCode = 503;
                    await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                    return;
                }

                await using (oaiResult)
                {
                    var oaiResponse = oaiResult.Response;
                    logContext.Log.BackendId = oaiResult.Backend.Id;

                    Response.StatusCode = (int)oaiResponse.StatusCode;
                    logContext.Log.StatusCode = Response.StatusCode;
                    logContext.Log.Success = oaiResponse.IsSuccessStatusCode;
                    logger.LogInformation("[{TraceId}] Received OpenAI response: {StatusCode} for chat model {Model}", HttpContext.TraceIdentifier, (int)oaiResponse.StatusCode, virtualModel.Name);

                    await using var oaiStream = await oaiResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                    if (input.Stream == true && oaiResponse.IsSuccessStatusCode)
                    {
                        // Translate: OpenAI SSE → Ollama NDJSON
                        var answerBuilder = new StringBuilder();
                        var toolCallAccumulator = new Dictionary<int, (string Name, StringBuilder Args)>();
                        using var sseReader = new StreamReader(oaiStream);
                        string? sseLine;

                        while ((sseLine = await sseReader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                        {
                            if (!sseLine.StartsWith("data: ")) continue;
                            var sseData = sseLine["data: ".Length..].Trim();
                            if (sseData == "[DONE]") break;

                            try
                            {
                                var oaiChunk = JsonNode.Parse(sseData);
                                if (oaiChunk == null) continue;

                                var choice = oaiChunk["choices"]?[0];
                                var delta = choice?["delta"];
                                var finishReason = choice?["finish_reason"]?.ToString();
                                var content = delta?["content"]?.ToString() ?? string.Empty;
                                if (!string.IsNullOrEmpty(content)) answerBuilder.Append(content);

                                // Accumulate streaming tool calls (OpenAI sends name + arguments in separate chunks)
                                var deltaToolCalls = delta?["tool_calls"]?.AsArray();
                                if (deltaToolCalls != null)
                                {
                                    foreach (var tc in deltaToolCalls)
                                    {
                                        var idx = tc?["index"]?.GetValue<int>() ?? 0;
                                        if (!toolCallAccumulator.TryGetValue(idx, out var acc))
                                        {
                                            acc = (string.Empty, new StringBuilder());
                                            toolCallAccumulator[idx] = acc;
                                        }
                                        var tcName = tc?["function"]?["name"]?.ToString();
                                        if (!string.IsNullOrEmpty(tcName))
                                            toolCallAccumulator[idx] = (tcName, toolCallAccumulator[idx].Args);
                                        var tcArgs = tc?["function"]?["arguments"]?.ToString();
                                        if (!string.IsNullOrEmpty(tcArgs))
                                            toolCallAccumulator[idx].Args.Append(tcArgs);
                                    }
                                }

                                var isDone = finishReason != null;
                                if (string.IsNullOrEmpty(content) && !isDone) continue;

                                var msgNode = new JsonObject { ["role"] = "assistant", ["content"] = content };

                                if (isDone && toolCallAccumulator.Count > 0)
                                {
                                    var tcArray = new JsonArray();
                                    foreach (var (_, (tcName, tcArgs)) in toolCallAccumulator.OrderBy(x => x.Key))
                                    {
                                        JsonNode? argsNode;
                                        try { argsNode = JsonNode.Parse(tcArgs.ToString()); }
                                        catch { argsNode = new JsonObject(); }
                                        tcArray.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = tcName, ["arguments"] = argsNode } });
                                    }
                                    msgNode["tool_calls"] = tcArray;
                                }

                                var ollamaChunk = new JsonObject { ["model"] = virtualModel.Name, ["message"] = msgNode, ["done"] = isDone };

                                if (isDone)
                                {
                                    var usage = oaiChunk["usage"];
                                    if (usage != null)
                                    {
                                        var pTokens = usage["prompt_tokens"]?.GetValue<long>() ?? 0;
                                        var cTokens = usage["completion_tokens"]?.GetValue<long>() ?? 0;
                                        ollamaChunk["prompt_eval_count"] = pTokens;
                                        ollamaChunk["eval_count"] = cTokens;
                                        logContext.Log.PromptTokens = (int)pTokens;
                                        logContext.Log.CompletionTokens = (int)cTokens;
                                        logContext.Log.TotalTokens = (int)(pTokens + cTokens);
                                    }
                                }

                                await Response.WriteAsync(ollamaChunk.ToJsonString() + "\n", HttpContext.RequestAborted);
                                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                            }
                            catch { /* skip malformed SSE chunk */ }
                        }

                        logContext.Log.Answer = answerBuilder.ToString();
                    }
                    else
                    {
                        // Non-streaming: Translate OpenAI JSON → Ollama JSON
                        using var oaiMs = new MemoryStream();
                        await oaiStream.CopyToAsync(oaiMs, HttpContext.RequestAborted);
                        oaiMs.Seek(0, SeekOrigin.Begin);

                        var contentReplaced = false;
                        try
                        {
                            var oaiResp = await JsonNode.ParseAsync(oaiMs, cancellationToken: HttpContext.RequestAborted);
                            if (oaiResp != null)
                            {
                                var content = oaiResp["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
                                var pTokens = oaiResp["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
                                var cTokens = oaiResp["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;

                                logContext.Log.Answer = content;
                                logContext.Log.PromptTokens = (int)pTokens;
                                logContext.Log.CompletionTokens = (int)cTokens;
                                logContext.Log.TotalTokens = (int)(pTokens + cTokens);

                                var msgNode = new JsonObject { ["role"] = "assistant", ["content"] = content };

                                var tcArr = oaiResp["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
                                if (tcArr != null && tcArr.Count > 0)
                                {
                                    var ollamaTcArr = new JsonArray();
                                    foreach (var tc in tcArr)
                                    {
                                        var argsStr = tc?["function"]?["arguments"]?.ToString() ?? "{}";
                                        JsonNode? argsNode;
                                        try { argsNode = JsonNode.Parse(argsStr); }
                                        catch { argsNode = new JsonObject(); }
                                        ollamaTcArr.Add(new JsonObject { ["function"] = new JsonObject { ["name"] = tc?["function"]?["name"]?.ToString(), ["arguments"] = argsNode } });
                                    }
                                    msgNode["tool_calls"] = ollamaTcArr;
                                }

                                var ollamaResp = new JsonObject
                                {
                                    ["model"] = virtualModel.Name,
                                    ["message"] = msgNode,
                                    ["done"] = true,
                                    ["prompt_eval_count"] = pTokens,
                                    ["eval_count"] = cTokens
                                };

                                Response.ContentType = "application/json";
                                await Response.WriteAsync(ollamaResp.ToJsonString(), HttpContext.RequestAborted);
                                contentReplaced = true;
                            }
                        }
                        catch { /* fallback to raw */ }

                        if (!contentReplaced)
                        {
                            oaiMs.Seek(0, SeekOrigin.Begin);
                            await oaiMs.CopyToAsync(Response.Body, HttpContext.RequestAborted);
                        }
                    }
                }
                return;
            }
            // ====================================================================
            // End OpenAI Backend Path — Ollama Backend Path continues below
            // ====================================================================

            // Re-read raw request body to preserve unknown fields (tools, tool_calls,
            // tool_choice, tool_call_id) that the typed OllamaRequestModel drops during
            // [FromBody] deserialization. Without this, the Ollama backend never sees
            // tool definitions and produces XML-like text instead of structured tool_calls.
            Request.Body.Position = 0;
            using var bodyReader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
            var rawBody = await bodyReader.ReadToEndAsync(HttpContext.RequestAborted);

            var forwardNode = JsonNode.Parse(rawBody) ?? new JsonObject();
            forwardNode["model"] = backend.UnderlyingModelName;
            if (virtualModel.Thinking.HasValue)
                forwardNode["think"] = virtualModel.Thinking.Value;
            forwardNode["keep_alive"] ??= backend.Provider.KeepAlive;

            if (virtualModel.NumCtx.HasValue || virtualModel.Temperature.HasValue ||
                virtualModel.TopP.HasValue || virtualModel.TopK.HasValue ||
                virtualModel.NumPredict.HasValue || virtualModel.RepeatPenalty.HasValue)
            {
                var opts = forwardNode["options"]?.AsObject() ?? new JsonObject();
                if (virtualModel.NumCtx.HasValue) opts["num_ctx"] = virtualModel.NumCtx.Value;
                if (virtualModel.Temperature.HasValue) opts["temperature"] = virtualModel.Temperature.Value;
                if (virtualModel.TopP.HasValue) opts["top_p"] = virtualModel.TopP.Value;
                if (virtualModel.TopK.HasValue) opts["top_k"] = virtualModel.TopK.Value;
                if (virtualModel.NumPredict.HasValue) opts["num_predict"] = virtualModel.NumPredict.Value;
                if (virtualModel.RepeatPenalty.HasValue) opts["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
                forwardNode["options"] = opts;
            }

            var result = await backendInvoker.SendAsync(
                virtualModel,
                backend,
                b =>
                {
                    forwardNode["model"] = b.UnderlyingModelName;
                    return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}/api/chat")
                    {
                        Content = new StringContent(forwardNode.ToJsonString(), Encoding.UTF8, "application/json")
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
                logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for chat request for model {Model}", HttpContext.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

                await using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                if (input.Stream == true && response.IsSuccessStatusCode)
                {
                    // Ollama native streaming: NDJSON (one JSON object per line)
                    var answerBuilder = new StringBuilder();
                    var thinkBuilder = new StringBuilder();
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
                                string? contentStr = chunkNode["message"]?["content"]?.ToString();
                                if (!string.IsNullOrEmpty(contentStr))
                                {
                                    answerBuilder.Append(contentStr);
                                }

                                string? thinkStr = chunkNode["message"]?["thinking"]?.ToString()
                                               ?? chunkNode["message"]?["think"]?.ToString();
                                if (!string.IsNullOrEmpty(thinkStr))
                                {
                                    thinkBuilder.Append(thinkStr);
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
                    logContext.Log.Thinking = thinkBuilder.ToString();
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

                            logContext.Log.Answer = resultNode["message"]?["content"]?.ToString() ?? string.Empty;
                            logContext.Log.Thinking = resultNode["message"]?["thinking"]?.ToString()
                                                   ?? resultNode["message"]?["think"]?.ToString()
                                                   ?? string.Empty;
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
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(logContext.Log.Model))
                activeRequestTracker.EndRequest(
                    logContext.Log.Model,
                    logContext.Log.ProviderId ?? 0,
                    logContext.Log.UnderlyingModelName,
                    logContext.Log.Success,
                    logContext.Log.Success ? string.Empty : ActiveRequestTracker.GetErrorSummary(logContext.Log.Answer),
                    logContext.Log.Answer);
        }
    }

    [HttpPost("generate")]
    public async Task Generate([FromBody] OllamaRequestModel input)
    {
        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            var modelToUse = string.IsNullOrWhiteSpace(input.Model)
                ? await globalSettingsService.GetDefaultChatModelAsync()
                : input.Model;

            VirtualModel? virtualModel = null;
            VirtualModelBackend? backend = null;

            if (modelToUse.StartsWith("physical_"))
            {
                var parts = modelToUse.Split('_');
                if (parts.Length >= 3 && int.TryParse(parts[1], out var providerId))
                {
                    if (!User.HasClaim(AppPermissions.Type, AppPermissionNames.CanChatWithUnderlyingModels))
                    {
                        Response.StatusCode = 403;
                        await Response.WriteAsync("Forbidden. You don't have permission to chat with underlying models.");
                        return;
                    }

                    var provider = await dbContext.OllamaProviders.FindAsync(providerId);
                    if (provider == null)
                    {
                        Response.StatusCode = 404;
                        await Response.WriteAsync($"Provider with ID {providerId} not found.");
                        return;
                    }

                    var underlyingModelName = string.Join('_', parts.Skip(2));
                    virtualModel = new VirtualModel
                    {
                        Name = modelToUse,
                        MaxRetries = 1,
                        HealthCheckTimeout = 40,
                    };
                    backend = new VirtualModelBackend
                    {
                        Provider = provider,
                        UnderlyingModelName = underlyingModelName,
                        ProviderId = providerId
                    };
                }
            }

            if (virtualModel == null)
            {
                virtualModel = await dbContext.VirtualModels
                    .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                    .FirstOrDefaultAsync(m => m.Name == modelToUse && m.Type == ModelType.Chat);

                if (virtualModel == null)
                {
                    Response.StatusCode = 404;
                    await Response.WriteAsync($"Model '{modelToUse}' not found in gateway.");
                    return;
                }

                backend = modelSelector.SelectBackend(virtualModel);
            }

            if (backend == null || backend.Provider == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync($"No available backend for model '{modelToUse}'.");
                return;
            }

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
                memoryUsageTracker.TrackApiKeyModelUsage(apiKeyId, virtualModel.Name);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            memoryUsageTracker.TrackVirtualModelUsage(virtualModel.Name);

            logContext.Log.Model = virtualModel.Name;
            logContext.Log.ConversationMessageCount = 1;
            logContext.Log.LastQuestion = input.Prompt ?? string.Empty;
            logContext.Log.ProviderId = backend.Provider.Id;
            logContext.Log.UnderlyingModelName = backend.UnderlyingModelName;
            activeRequestTracker.StartRequest(virtualModel.Name, logContext.Log.LastQuestion, backend.Provider.Id, backend.UnderlyingModelName, logContext.Log.ApiKeyName);

            // /api/generate has no OpenAI-compatible equivalent — reject if backend is OpenAI
            if (backend.Provider.ProviderType == ProviderType.OpenAI)
            {
                Response.StatusCode = 501;
                await Response.WriteAsync("The /api/generate endpoint is not supported for OpenAI-compatible providers. Use /api/chat or /v1/chat/completions instead.");
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
            if (!string.IsNullOrEmpty(logContext.Log.Model))
                activeRequestTracker.EndRequest(
                    logContext.Log.Model,
                    logContext.Log.ProviderId ?? 0,
                    logContext.Log.UnderlyingModelName,
                    logContext.Log.Success,
                    logContext.Log.Success ? string.Empty : ActiveRequestTracker.GetErrorSummary(logContext.Log.Answer),
                    logContext.Log.Answer);
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
            string? modelName = inputNode["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = await globalSettingsService.GetDefaultEmbeddingModelAsync();
            }

            var virtualModel = await dbContext.VirtualModels
                .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                .FirstOrDefaultAsync(m => m.Name == modelName && m.Type == ModelType.Embedding);

            if (virtualModel == null)
            {
                Response.StatusCode = 404;
                await Response.WriteAsync($"Embedding model '{modelName}' not found in gateway.");
                return;
            }

            var backend = modelSelector.SelectBackend(virtualModel);
            if (backend == null || backend.Provider == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync($"No available backend for model '{modelName}'.");
                return;
            }

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
                memoryUsageTracker.TrackApiKeyModelUsage(apiKeyId, virtualModel.Name);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            memoryUsageTracker.TrackVirtualModelUsage(virtualModel.Name);

            logContext.Log.Model = virtualModel.Name;
            logContext.Log.ConversationMessageCount = 1;
            logContext.Log.LastQuestion = inputNode["input"]?.ToString() ?? inputNode["prompt"]?.ToString() ?? string.Empty;
            logContext.Log.ProviderId = backend.Provider.Id;
            logContext.Log.UnderlyingModelName = backend.UnderlyingModelName;
            activeRequestTracker.StartRequest(virtualModel.Name, logContext.Log.LastQuestion, backend.Provider.Id, backend.UnderlyingModelName, logContext.Log.ApiKeyName);

            // ====================================================================
            // OpenAI-compatible Backend Path (Ollama embed request → OpenAI downstream)
            // ====================================================================
            if (backend.Provider.ProviderType == ProviderType.OpenAI)
            {
                var openAiEmbedReq = new JsonObject
                {
                    ["input"] = inputNode["input"]?.DeepClone() ?? inputNode["prompt"]?.DeepClone()
                };

                var oaiResult = await backendInvoker.SendAsync(
                    virtualModel,
                    backend,
                    b =>
                    {
                        openAiEmbedReq["model"] = b.UnderlyingModelName;
                        return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}/v1/embeddings")
                        {
                            Content = new StringContent(openAiEmbedReq.ToJsonString(), Encoding.UTF8, "application/json")
                        };
                    },
                    HttpContext.RequestAborted);

                if (oaiResult == null)
                {
                    Response.StatusCode = 503;
                    await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                    return;
                }

                await using (oaiResult)
                {
                    var oaiEmbedResponse = oaiResult.Response;
                    backend = oaiResult.Backend;
                    logContext.Log.BackendId = backend.Id;

                    Response.StatusCode = (int)oaiEmbedResponse.StatusCode;
                    logContext.Log.StatusCode = Response.StatusCode;
                    logContext.Log.Success = oaiEmbedResponse.IsSuccessStatusCode;

                    if (!oaiEmbedResponse.IsSuccessStatusCode)
                    {
                        var errContent = await oaiEmbedResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                        logContext.Log.Answer = errContent;
                        await Response.WriteAsync(errContent, HttpContext.RequestAborted);
                        return;
                    }

                    // Translate: OpenAI response { data: [{ embedding: [...] }] } → Ollama { embeddings: [[...]] }
                    var oaiEmbedContent = await oaiEmbedResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    try
                    {
                        var oaiRespNode = JsonNode.Parse(oaiEmbedContent);
                        if (oaiRespNode != null)
                        {
                            var dataArr = oaiRespNode["data"]?.AsArray();
                            var embeddingsArr = new JsonArray();
                            if (dataArr != null)
                                foreach (var item in dataArr)
                                    embeddingsArr.Add(item?["embedding"]?.DeepClone());

                            var pTokens = oaiRespNode["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
                            logContext.Log.PromptTokens = (int)pTokens;
                            logContext.Log.TotalTokens = (int)pTokens;

                            var ollamaEmbedResp = new JsonObject
                            {
                                ["model"] = virtualModel.Name,
                                ["embeddings"] = embeddingsArr
                            };
                            Response.ContentType = "application/json";
                            await Response.WriteAsync(ollamaEmbedResp.ToJsonString(), HttpContext.RequestAborted);
                        }
                    }
                    catch
                    {
                        await Response.WriteAsync(oaiEmbedContent, HttpContext.RequestAborted);
                    }
                }
                return;
            }
            // ====================================================================
            // End OpenAI Embed Path — Ollama Embed Path continues below
            // ====================================================================

            inputNode["keep_alive"] = backend.Provider.KeepAlive;

            if (virtualModel.NumCtx.HasValue || virtualModel.Temperature.HasValue || virtualModel.TopP.HasValue || virtualModel.TopK.HasValue || virtualModel.RepeatPenalty.HasValue)
            {
                var optionsNode = inputNode["options"]?.AsObject() ?? new JsonObject();
                if (virtualModel.NumCtx.HasValue) optionsNode["num_ctx"] = virtualModel.NumCtx.Value;
                if (virtualModel.Temperature.HasValue) optionsNode["temperature"] = virtualModel.Temperature.Value;
                if (virtualModel.TopP.HasValue) optionsNode["top_p"] = virtualModel.TopP.Value;
                if (virtualModel.TopK.HasValue) optionsNode["top_k"] = virtualModel.TopK.Value;
                if (virtualModel.RepeatPenalty.HasValue) optionsNode["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
                inputNode["options"] = optionsNode;
            }

            var result = await backendInvoker.SendAsync(
                virtualModel,
                backend,
                b =>
                {
                    inputNode["model"] = b.UnderlyingModelName;
                    return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}/api/embed")
                    {
                        Content = new StringContent(inputNode.ToJsonString(), Encoding.UTF8, "application/json")
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
                logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for embedding request for model {Model}", HttpContext.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

                if (!response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    logContext.Log.Answer = content;
                    await Response.WriteAsync(content, HttpContext.RequestAborted);
                }
                else
                {
                    var resultNode = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted), cancellationToken: HttpContext.RequestAborted);
                    if (resultNode != null)
                    {
                        resultNode["model"] = virtualModel.Name;
                        await Response.WriteAsync(resultNode.ToJsonString(), HttpContext.RequestAborted);
                    }
                }
            }
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
            if (!string.IsNullOrEmpty(logContext.Log.Model))
                activeRequestTracker.EndRequest(
                    logContext.Log.Model,
                    logContext.Log.ProviderId ?? 0,
                    logContext.Log.UnderlyingModelName,
                    logContext.Log.Success,
                    logContext.Log.Success ? string.Empty : ActiveRequestTracker.GetErrorSummary(logContext.Log.Answer),
                    logContext.Log.Answer);
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
