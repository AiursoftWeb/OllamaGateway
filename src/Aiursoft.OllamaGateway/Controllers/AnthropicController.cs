using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Models.AnthropicViewModels;
using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
[RequiresUserOrApiKeyAuth]
public class AnthropicController : ControllerBase
{
    private readonly TemplateDbContext _dbContext;
    private readonly RequestLogContext _logContext;
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly ILogger<AnthropicController> _logger;
    private readonly MemoryUsageTracker _memoryUsageTracker;
    private readonly IModelSelector _modelSelector;
    private readonly ActiveRequestTracker _activeRequestTracker;
    private readonly IBackendInvoker _backendInvoker;

    public AnthropicController(
        TemplateDbContext dbContext,
        RequestLogContext logContext,
        GlobalSettingsService globalSettingsService,
        ILogger<AnthropicController> logger,
        MemoryUsageTracker memoryUsageTracker,
        IModelSelector modelSelector,
        ActiveRequestTracker activeRequestTracker,
        IBackendInvoker backendInvoker)
    {
        _dbContext = dbContext;
        _logContext = logContext;
        _globalSettingsService = globalSettingsService;
        _logger = logger;
        _memoryUsageTracker = memoryUsageTracker;
        _modelSelector = modelSelector;
        _activeRequestTracker = activeRequestTracker;
        _backendInvoker = backendInvoker;
    }

    private string ExtractTextFromBlocks(object? content)
    {
        if (content == null) return string.Empty;

        // Handle Newtonsoft.Json.Linq types if they arrive from the binder
        if (content is Newtonsoft.Json.Linq.JToken jToken)
        {
            if (jToken is Newtonsoft.Json.Linq.JValue jValue && jValue.Type == Newtonsoft.Json.Linq.JTokenType.String)
            {
                return jValue.Value?.ToString() ?? string.Empty;
            }

            if (jToken is Newtonsoft.Json.Linq.JArray jArray)
            {
                var parts = new List<string>();
                foreach (var item in jArray)
                {
                    if (item is Newtonsoft.Json.Linq.JObject obj)
                    {
                        var type = obj["type"]?.ToString();
                        if (type == "text")
                        {
                            var text = obj["text"]?.ToString();
                            if (!string.IsNullOrEmpty(text)) parts.Add(text);
                        }
                        else if (type == "tool_use" || type == "tool_result")
                        {
                            var inner = obj["content"] ?? obj["input"];
                            parts.Add(ExtractTextFromBlocks(inner));
                        }
                    }
                }
                return string.Join("\n", parts);
            }

            return jToken.ToString(Newtonsoft.Json.Formatting.None);
        }

        var node = JsonSerializer.SerializeToNode(content);
        if (node == null) return string.Empty;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var str)) return str;
        if (node is JsonArray arr)
        {
            var parts = new List<string>();
            foreach (var item in arr)
            {
                if (item == null) continue;
                if (item is JsonValue itemJv && itemJv.TryGetValue<string>(out var itemStr))
                {
                    parts.Add(itemStr);
                }
                else if (item is JsonObject obj)
                {
                    var type = obj["type"]?.ToString();
                    if (type == "text")
                    {
                        var text = obj["text"]?.ToString();
                        if (!string.IsNullOrEmpty(text)) parts.Add(text);
                    }
                    else if (type == "tool_use" || type == "tool_result")
                    {
                        var inner = obj["content"] ?? obj["input"];
                        parts.Add(ExtractTextFromBlocks(inner));
                    }
                }
            }
            return string.Join("\n", parts);
        }
        return node.ToString();
    }

    [HttpPost("/v1/messages")]
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

        VirtualModel? virtualModel = null;
        VirtualModelBackend? backend = null;

        try
        {
            var modelToUse = string.IsNullOrWhiteSpace(request.Model)
                ? await _globalSettingsService.GetDefaultChatModelAsync()
                : request.Model;

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

                    var provider = await _dbContext.OllamaProviders.FindAsync(providerId);
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
                virtualModel = await _dbContext.VirtualModels
                    .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                    .FirstOrDefaultAsync(m => m.Name == modelToUse && m.Type == ModelType.Chat);

                if (virtualModel == null)
                {
                    Response.StatusCode = 404;
                    await Response.WriteAsync($"Model '{modelToUse}' not found in gateway.");
                    return;
                }

                backend = _modelSelector.SelectBackend(virtualModel);
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

            // Translate Anthropic -> OpenAI Format for the backend
            var openaiMessages = new JsonArray();
            if (request.System != null)
            {
                var sysText = ExtractTextFromBlocks(request.System);
                openaiMessages.Add(new JsonObject { ["role"] = "system", ["content"] = sysText });
            }

            foreach (var msg in request.Messages)
            {
                var contentNode = msg.Content is Newtonsoft.Json.Linq.JToken jt
                    ? JsonNode.Parse(jt.ToString(Newtonsoft.Json.Formatting.None))
                    : JsonSerializer.SerializeToNode(msg.Content);

                if (contentNode is JsonArray arr)
                {
                    if (msg.Role == "user")
                    {
                        var textParts = new List<string>();
                        foreach (var item in arr)
                        {
                            if (item is JsonObject obj)
                            {
                                var type = obj["type"]?.ToString();
                                if (type == "text")
                                {
                                    var text = obj["text"]?.ToString();
                                    if (!string.IsNullOrEmpty(text)) textParts.Add(text);
                                }
                                else if (type == "tool_result")
                                {
                                    if (textParts.Count > 0)
                                    {
                                        openaiMessages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
                                        textParts.Clear();
                                    }

                                    var toolUseId = obj["tool_use_id"]?.ToString() ?? string.Empty;
                                    var innerContent = ExtractTextFromBlocks(obj["content"]);
                                    var isError = obj["is_error"]?.GetValue<bool>() == true;

                                    openaiMessages.Add(new JsonObject
                                    {
                                        ["role"] = "tool",
                                        ["tool_call_id"] = toolUseId,
                                        ["content"] = isError ? $"Error: {innerContent}" : innerContent
                                    });
                                }
                            }
                            else if (item is JsonValue jv && jv.TryGetValue<string>(out var str))
                            {
                                textParts.Add(str);
                            }
                        }
                        if (textParts.Count > 0)
                        {
                            openaiMessages.Add(new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", textParts) });
                        }
                    }
                    else if (msg.Role == "assistant")
                    {
                        var textParts = new List<string>();
                        var toolCalls = new JsonArray();
                        string? reasoningContent = null;

                        foreach (var item in arr)
                        {
                            if (item is JsonObject obj)
                            {
                                var type = obj["type"]?.ToString();
                                if (type == "text")
                                {
                                    var text = obj["text"]?.ToString();
                                    if (!string.IsNullOrEmpty(text)) textParts.Add(text);
                                }
                                else if (type == "thinking")
                                {
                                    reasoningContent = obj["thinking"]?.ToString();
                                }
                                else if (type == "tool_use")
                                {
                                    var id = obj["id"]?.ToString() ?? string.Empty;
                                    var name = obj["name"]?.ToString() ?? string.Empty;
                                    var inputNode = obj["input"];
                                    var inputStr = inputNode?.ToJsonString() ?? "{}";

                                    toolCalls.Add(new JsonObject
                                    {
                                        ["id"] = id,
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = name,
                                            ["arguments"] = inputStr
                                        }
                                    });
                                }
                            }
                            else if (item is JsonValue jv && jv.TryGetValue<string>(out var str))
                            {
                                textParts.Add(str);
                            }
                        }

                        var fullText = string.Join("\n", textParts);
                        if (string.IsNullOrEmpty(reasoningContent))
                        {
                            var thinkStartIndex = fullText.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                            if (thinkStartIndex >= 0)
                            {
                                var thinkEndIndex = fullText.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                                if (thinkEndIndex > thinkStartIndex)
                                {
                                    reasoningContent = fullText.Substring(thinkStartIndex + 7, thinkEndIndex - thinkStartIndex - 7).Trim();
                                    fullText = fullText.Remove(thinkStartIndex, thinkEndIndex + 8 - thinkStartIndex).Trim();
                                }
                                else
                                {
                                    reasoningContent = fullText.Substring(thinkStartIndex + 7).Trim();
                                    fullText = fullText.Substring(0, thinkStartIndex).Trim();
                                }
                            }
                        }
                        // Fallback: use top-level reasoning_content on the message if thinking
                        // blocks and <think> tags weren't found. Some clients (Open WebUI, etc.)
                        // send reasoning_content this way for multi-turn continuity.
                        if (string.IsNullOrEmpty(reasoningContent) && !string.IsNullOrEmpty(msg.ReasoningContent))
                        {
                            reasoningContent = msg.ReasoningContent;
                        }

                        var assistantMsg = new JsonObject { ["role"] = "assistant", ["content"] = fullText };
                        if (!string.IsNullOrEmpty(reasoningContent))
                        {
                            assistantMsg["reasoning_content"] = reasoningContent;
                        }
                        if (toolCalls.Count > 0)
                        {
                            assistantMsg["tool_calls"] = toolCalls;
                        }
                        openaiMessages.Add(assistantMsg);
                    }
                    else
                    {
                        var text = ExtractTextFromBlocks(msg.Content);
                        openaiMessages.Add(new JsonObject { ["role"] = msg.Role, ["content"] = text });
                    }
                }
                else
                {
                    var text = ExtractTextFromBlocks(msg.Content);
                    openaiMessages.Add(new JsonObject { ["role"] = msg.Role, ["content"] = text });
                }
            }

            var openaiBody = new JsonObject
            {
                ["model"] = backend.UnderlyingModelName,
                ["messages"] = openaiMessages,
                ["stream"] = isStream
            };

            // Apply overrides
            if (request.MaxTokens.HasValue) openaiBody["max_tokens"] = request.MaxTokens.Value;
            if (request.Temperature.HasValue) openaiBody["temperature"] = request.Temperature.Value;
            if (request.TopP.HasValue) openaiBody["top_p"] = request.TopP.Value;

            if (virtualModel.Temperature.HasValue) openaiBody["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) openaiBody["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.NumPredict.HasValue) openaiBody["max_tokens"] = virtualModel.NumPredict.Value;
            if (virtualModel.Thinking.HasValue)
                openaiBody["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = virtualModel.Thinking.Value };

            if (request.Tools != null && request.Tools.Count > 0)
            {
                var toolsArray = new JsonArray();
                foreach (var tool in request.Tools)
                {
                    JsonNode? parameters;
                    if (tool.InputSchema is Newtonsoft.Json.Linq.JToken jToken)
                    {
                        parameters = JsonNode.Parse(jToken.ToString(Newtonsoft.Json.Formatting.None));
                    }
                    else
                    {
                        parameters = JsonSerializer.SerializeToNode(tool.InputSchema);
                    }

                    toolsArray.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description ?? "",
                            ["parameters"] = parameters ?? new JsonObject()
                        }
                    });
                }
                openaiBody["tools"] = toolsArray;
            }

            var isOllamaDirect = backend.Provider.ProviderType == ProviderType.Ollama;
            var targetEndpoint = "/v1/chat/completions";

            JsonObject requestBody = openaiBody;
            if (isOllamaDirect)
            {
                targetEndpoint = "/api/chat";
                var clonedMessages = openaiMessages.DeepClone().AsArray();
                foreach (var msgNode in clonedMessages)
                {
                    if (msgNode is JsonObject msgObj)
                    {
                        if (msgObj["tool_calls"] is JsonArray toolCalls)
                        {
                            foreach (var tc in toolCalls)
                            {
                                if (tc is JsonObject tcObj && tcObj["function"] is JsonObject funcObj)
                                {
                                    var argsStr = funcObj["arguments"]?.ToString();
                                    if (!string.IsNullOrWhiteSpace(argsStr))
                                    {
                                        try { funcObj["arguments"] = JsonNode.Parse(argsStr); }
                                        catch { funcObj["arguments"] = new JsonObject(); }
                                    }
                                    else
                                    {
                                        funcObj["arguments"] = new JsonObject();
                                    }
                                }
                            }
                        }
                    }
                }

                requestBody = new JsonObject
                {
                    ["model"] = backend.UnderlyingModelName,
                    ["messages"] = clonedMessages,
                    ["stream"] = isStream
                };
                if (openaiBody["tools"] != null) requestBody["tools"] = openaiBody["tools"]!.DeepClone();

                var options = new JsonObject();
                if (openaiBody["temperature"] != null) options["temperature"] = openaiBody["temperature"]!.DeepClone();
                if (openaiBody["top_p"] != null) options["top_p"] = openaiBody["top_p"]!.DeepClone();
                if (openaiBody["max_tokens"] != null) options["num_predict"] = openaiBody["max_tokens"]!.DeepClone();

                if (virtualModel.TopK.HasValue) options["top_k"] = virtualModel.TopK.Value;
                if (virtualModel.NumCtx.HasValue) options["num_ctx"] = virtualModel.NumCtx.Value;
                if (virtualModel.RepeatPenalty.HasValue) options["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
                if (virtualModel.Thinking.HasValue) requestBody["think"] = virtualModel.Thinking.Value;
                if (options.Count > 0) requestBody["options"] = options;
            }

            var result = await _backendInvoker.SendAsync(
                virtualModel,
                backend,
                b =>
                {
                    requestBody["model"] = b.UnderlyingModelName;
                    return new HttpRequestMessage(HttpMethod.Post, $"{b.Provider!.BaseUrl.TrimEnd('/')}{targetEndpoint}")
                    {
                        Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                },
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

                await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                if (isStream)
                {
                    Response.ContentType = "text/event-stream";
                    Response.Headers.CacheControl = "no-cache";
                    var answerBuilder = new StringBuilder();
                    using var reader = new StreamReader(upstreamStream);
                    string? sLine;
                    var msgId = $"msg_{Guid.NewGuid():N}";

                    // Emitting message_start
                    var messageStart = new JsonObject
                    {
                        ["type"] = "message_start",
                        ["message"] = new JsonObject
                        {
                            ["id"] = msgId,
                            ["type"] = "message",
                            ["role"] = "assistant",
                            ["model"] = virtualModel.Name,
                            ["content"] = new JsonArray(),
                            ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
                        }
                    };
                    await Response.WriteAsync($"event: message_start\ndata: {messageStart.ToJsonString()}\n\n", HttpContext.RequestAborted);

                    var activeToolBlocks = new HashSet<int>();
                    var currentStopReason = "end_turn";
                    var localToolIndexCounter = 0;

                    // Thinking block state – we defer content_block_start until the first
                    // chunk so we can emit a thinking block when the backend sends
                    // reasoning_content, and a text block otherwise.
                    var thinkingBlockStarted = false;
                    var thinkingBlockStopped = false;
                    var thinkingSignature = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
                    const int thinkingBlockIndex = 0;
                    var textBlockIndex = 0; // 0 when no thinking, shifts to 1 after thinking stops
                    var textBlockStarted = false;

                    while ((sLine = await reader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(sLine)) continue;

                        string deltaText = "";
                        JsonArray? toolCalls = null;

                        string? thinkingDelta = null;

                        if (isOllamaDirect)
                        {
                            try
                            {
                                var chunk = JsonNode.Parse(sLine);
                                if (chunk != null)
                                {
                                    deltaText = chunk["message"]?["content"]?.ToString() ?? "";
                                    toolCalls = chunk["message"]?["tool_calls"]?.AsArray();
                                    if (toolCalls != null && toolCalls.Count > 0) currentStopReason = "tool_use";

                                    if (chunk["done"]?.GetValue<bool>() == true)
                                    {
                                        var pTok = chunk["prompt_eval_count"]?.GetValue<long>() ?? 0;
                                        var cTok = chunk["eval_count"]?.GetValue<long>() ?? 0;
                                        _logContext.Log.PromptTokens = (int)pTok;
                                        _logContext.Log.CompletionTokens = (int)cTok;

                                        var doneReason = chunk["done_reason"]?.ToString();
                                        if (doneReason == "length") currentStopReason = "max_tokens";
                                    }
                                }
                            }
                            catch (JsonException) { }
                        }
                        else
                        {
                            if (sLine.StartsWith("data: ") && sLine != "data: [DONE]")
                            {
                                try
                                {
                                    var chunk = JsonNode.Parse(sLine["data: ".Length..]);
                                    if (chunk != null)
                                    {
                                        var choice = chunk["choices"]?[0];
                                        deltaText = choice?["delta"]?["content"]?.ToString() ?? "";

                                        // Translate OpenAI reasoning_content → Anthropic thinking block.
                                        // We emit content_block_start/delta/stop for thinking instead of
                                        // wrapping in <think> tags inside text_delta events.
                                        var reasoningNode = choice?["delta"]?["reasoning_content"];
                                        if (reasoningNode != null)
                                        {
                                            if (!thinkingBlockStarted)
                                            {
                                                thinkingBlockStarted = true;
                                                textBlockIndex = 1; // text block shifts to index 1
                                                var thinkingStart = new JsonObject
                                                {
                                                    ["type"] = "content_block_start",
                                                    ["index"] = thinkingBlockIndex,
                                                    ["content_block"] = new JsonObject
                                                    {
                                                        ["type"] = "thinking",
                                                        ["thinking"] = "",
                                                        ["signature"] = thinkingSignature
                                                    }
                                                };
                                                await Response.WriteAsync(
                                                    $"event: content_block_start\ndata: {thinkingStart.ToJsonString()}\n\n",
                                                    HttpContext.RequestAborted);
                                            }

                                            thinkingDelta = reasoningNode.ToString();
                                        }
                                        else if (thinkingBlockStarted && !thinkingBlockStopped)
                                        {
                                            // Reasoning stream ended — close the thinking block.
                                            thinkingBlockStopped = true;
                                            var thinkingStop = new JsonObject
                                            {
                                                ["type"] = "content_block_stop",
                                                ["index"] = thinkingBlockIndex
                                            };
                                            await Response.WriteAsync(
                                                $"event: content_block_stop\ndata: {thinkingStop.ToJsonString()}\n\n",
                                                HttpContext.RequestAborted);
                                        }

                                        toolCalls = choice?["delta"]?["tool_calls"]?.AsArray();
                                        var finishReason = choice?["finish_reason"]?.ToString();
                                        if (finishReason == "length") currentStopReason = "max_tokens";
                                        else if (finishReason == "tool_calls" || finishReason == "function_call") currentStopReason = "tool_use";
                                        else if (finishReason == "stop") currentStopReason = "end_turn";

                                        if (chunk["usage"] != null)
                                        {
                                            var pTok = chunk["usage"]!["prompt_tokens"]?.GetValue<long>() ?? 0;
                                            var cTok = chunk["usage"]!["completion_tokens"]?.GetValue<long>() ?? 0;
                                            _logContext.Log.PromptTokens = (int)pTok;
                                            _logContext.Log.CompletionTokens = (int)cTok;
                                        }
                                    }
                                }
                                catch (JsonException) { }
                            }
                        }

                        // Start a text block lazily when text first appears (but not yet started).
                        if (!textBlockStarted && !string.IsNullOrEmpty(deltaText))
                        {
                            textBlockStarted = true;
                            var textStart = new JsonObject
                            {
                                ["type"] = "content_block_start",
                                ["index"] = textBlockIndex,
                                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" }
                            };
                            await Response.WriteAsync(
                                $"event: content_block_start\ndata: {textStart.ToJsonString()}\n\n",
                                HttpContext.RequestAborted);
                        }

                        // Emit thinking delta (separate from text delta).
                        if (!string.IsNullOrEmpty(thinkingDelta))
                        {
                            var thinkingDeltaObj = new JsonObject
                            {
                                ["type"] = "content_block_delta",
                                ["index"] = thinkingBlockIndex,
                                ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinkingDelta }
                            };
                            await Response.WriteAsync(
                                $"event: content_block_delta\ndata: {thinkingDeltaObj.ToJsonString()}\n\n",
                                HttpContext.RequestAborted);
                        }

                        // Emit text delta.
                        if (!string.IsNullOrEmpty(deltaText))
                        {
                            answerBuilder.Append(deltaText);
                            var deltaObj = new JsonObject
                            {
                                ["type"] = "content_block_delta",
                                ["index"] = textBlockIndex,
                                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = deltaText }
                            };
                            await Response.WriteAsync($"event: content_block_delta\ndata: {deltaObj.ToJsonString()}\n\n", HttpContext.RequestAborted);
                        }

                        if (toolCalls != null)
                        {
                            foreach (var tc in toolCalls)
                            {
                                var tcIndexNode = tc?["index"];
                                var index = tcIndexNode != null ? tcIndexNode.GetValue<int>() : localToolIndexCounter++;
                                var id = tc?["id"]?.ToString();
                                var funcName = tc?["function"]?["name"]?.ToString();
                                var argsDelta = tc?["function"]?["arguments"]?.ToString();

                                // Start a new tool block if we haven't seen this index yet
                                if (!activeToolBlocks.Contains(index) && !string.IsNullOrEmpty(id))
                                {
                                    activeToolBlocks.Add(index);
                                    var toolStart = new JsonObject
                                    {
                                        ["type"] = "content_block_start",
                                        ["index"] = textBlockIndex + 1 + index,
                                        ["content_block"] = new JsonObject
                                        {
                                            ["type"] = "tool_use",
                                            ["id"] = id,
                                            ["name"] = funcName ?? "unknown",
                                            ["input"] = new JsonObject()
                                        }
                                    };
                                    await Response.WriteAsync($"event: content_block_start\ndata: {toolStart.ToJsonString()}\n\n", HttpContext.RequestAborted);
                                }

                                if (!string.IsNullOrEmpty(argsDelta))
                                {
                                    var toolDelta = new JsonObject
                                    {
                                        ["type"] = "content_block_delta",
                                        ["index"] = textBlockIndex + 1 + index,
                                        ["delta"] = new JsonObject
                                        {
                                            ["type"] = "input_json_delta",
                                            ["partial_json"] = argsDelta
                                        }
                                    };
                                    await Response.WriteAsync($"event: content_block_delta\ndata: {toolDelta.ToJsonString()}\n\n", HttpContext.RequestAborted);
                                }
                            }
                        }
                        await Response.Body.FlushAsync(HttpContext.RequestAborted);
                    }

                    // Close all blocks
                    // Thinking block (if started and not already stopped mid-stream)
                    if (thinkingBlockStarted && !thinkingBlockStopped)
                    {
                        var thinkingStop = new JsonObject { ["type"] = "content_block_stop", ["index"] = thinkingBlockIndex };
                        await Response.WriteAsync($"event: content_block_stop\ndata: {thinkingStop.ToJsonString()}\n\n", HttpContext.RequestAborted);
                    }
                    // Text block
                    if (textBlockStarted)
                    {
                        var textStop = new JsonObject { ["type"] = "content_block_stop", ["index"] = textBlockIndex };
                        await Response.WriteAsync($"event: content_block_stop\ndata: {textStop.ToJsonString()}\n\n", HttpContext.RequestAborted);
                    }
                    // Tool blocks
                    foreach (var idx in activeToolBlocks)
                    {
                        var stop = new JsonObject { ["type"] = "content_block_stop", ["index"] = textBlockIndex + 1 + idx };
                        await Response.WriteAsync($"event: content_block_stop\ndata: {stop.ToJsonString()}\n\n", HttpContext.RequestAborted);
                    }

                    var messageDelta = new JsonObject
                    {
                        ["type"] = "message_delta",
                        ["delta"] = new JsonObject { ["stop_reason"] = currentStopReason },
                        ["usage"] = new JsonObject { ["output_tokens"] = _logContext.Log.CompletionTokens }
                    };
                    await Response.WriteAsync($"event: message_delta\ndata: {messageDelta.ToJsonString()}\n\n", HttpContext.RequestAborted);

                    var messageStop = new JsonObject { ["type"] = "message_stop" };
                    await Response.WriteAsync($"event: message_stop\ndata: {messageStop.ToJsonString()}\n\n", HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);

                    _logContext.Log.Answer = answerBuilder.ToString();
                }
                else
                {
                    using var directMs = new MemoryStream();
                    await upstreamStream.CopyToAsync(directMs, HttpContext.RequestAborted);
                    directMs.Seek(0, SeekOrigin.Begin);
                    try
                    {
                        var respNode = await JsonNode.ParseAsync(directMs, cancellationToken: HttpContext.RequestAborted);
                        if (respNode != null)
                        {
                            string respContent;
                            string stopReason = "end_turn";
                            var contentBlocks = new List<AnthropicContentBlock>();
                            long pTok, cTok;

                            if (isOllamaDirect)
                            {
                                respContent = respNode["message"]?["content"]?.ToString() ?? "";
                                pTok = respNode["prompt_eval_count"]?.GetValue<long>() ?? 0;
                                cTok = respNode["eval_count"]?.GetValue<long>() ?? 0;

                                var doneReason = respNode["done_reason"]?.ToString();
                                if (doneReason == "length") stopReason = "max_tokens";

                                // Handle Ollama tool calls if present
                                var toolCalls = respNode["message"]?["tool_calls"]?.AsArray();
                                if (toolCalls != null && toolCalls.Count > 0)
                                {
                                    stopReason = "tool_use";
                                    foreach (var tc in toolCalls)
                                    {
                                        if (tc == null) continue;
                                        contentBlocks.Add(new AnthropicContentBlock
                                        {
                                            Type = "tool_use",
                                            Id = tc["id"]?.ToString() ?? $"toolu_{Guid.NewGuid():N}",
                                            Name = tc["function"]?["name"]?.ToString() ?? "unknown",
                                            Input = tc["function"]?["arguments"]?.DeepClone()
                                        });
                                    }
                                }
                            }
                            else
                            {
                                var message = respNode["choices"]?[0]?["message"];
                                respContent = message?["content"]?.ToString() ?? "";
                                var respReasoningContent = message?["reasoning_content"]?.ToString() ?? "";
                                var oaiFinish = respNode["choices"]?[0]?["finish_reason"]?.ToString();
                                if (oaiFinish == "length") stopReason = "max_tokens";
                                else if (oaiFinish == "tool_calls" || oaiFinish == "function_call") stopReason = "tool_use";
                                else if (oaiFinish == "stop") stopReason = "end_turn";

                                pTok = respNode["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
                                cTok = respNode["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;

                                var toolCalls = message?["tool_calls"]?.AsArray();
                                if (toolCalls != null && toolCalls.Count > 0)
                                {
                                    stopReason = "tool_use";
                                    foreach (var tc in toolCalls)
                                    {
                                        if (tc == null) continue;
                                        JsonNode? args;
                                        try
                                        {
                                            args = JsonNode.Parse(tc["function"]?["arguments"]?.ToString() ?? "{}");
                                        }
                                        catch { args = new JsonObject(); }

                                        contentBlocks.Add(new AnthropicContentBlock
                                        {
                                            Type = "tool_use",
                                            Id = tc["id"]?.ToString() ?? $"toolu_{Guid.NewGuid():N}",
                                            Name = tc["function"]?["name"]?.ToString() ?? "unknown",
                                            Input = args
                                        });
                                    }
                                }

                            // Insert content blocks in reverse order so the final array is:
                            //   [thinking, text, ...tool_calls...]
                            // First: text (if present).
                            if (!string.IsNullOrEmpty(respContent))
                            {
                                contentBlocks.Insert(0, new AnthropicContentBlock { Type = "text", Text = respContent });
                            }
                            // Second: thinking (if present) — Insert(0) puts it before text.
                            // The official Anthropic API requires thinking blocks with a non-empty
                            // signature to be passed back in subsequent requests for multi-turn
                            // continuity. Since the backend (OpenAI) does not provide an Anthropic
                            // signature, we generate an opaque token that the gateway passes through
                            // unchanged.
                            if (!string.IsNullOrEmpty(respReasoningContent))
                            {
                                contentBlocks.Insert(0, new AnthropicContentBlock
                                {
                                    Type = "thinking",
                                    Thinking = respReasoningContent,
                                    Signature = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                                });
                            }
                            }

                            _logContext.Log.Answer = respContent;
                            _logContext.Log.PromptTokens = (int)pTok;
                            _logContext.Log.CompletionTokens = (int)cTok;
                            _logContext.Log.TotalTokens = (int)(pTok + cTok);

                            var anthropicResponse = new AnthropicResponse
                            {
                                Id = $"msg_{Guid.NewGuid():N}",
                                Model = virtualModel.Name,
                                StopReason = stopReason,
                                Usage = new AnthropicUsage { InputTokens = (int)pTok, OutputTokens = (int)cTok },
                                Content = contentBlocks
                            };

                            Response.ContentType = "application/json";
                            await Response.WriteAsync(JsonSerializer.Serialize(anthropicResponse), HttpContext.RequestAborted);
                        }
                    }
                    catch
                    {
                        directMs.Seek(0, SeekOrigin.Begin);
                        var rawResp = await new StreamReader(directMs).ReadToEndAsync(HttpContext.RequestAborted);
                        _logContext.Log.Answer = rawResp;
                        Response.ContentType = "application/json";
                        await Response.WriteAsync(rawResp, HttpContext.RequestAborted);
                    }
                }
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
