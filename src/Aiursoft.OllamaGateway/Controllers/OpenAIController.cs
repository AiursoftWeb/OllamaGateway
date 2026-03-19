using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Controllers;

[AllowAnonymous]
public class OpenAIController : ControllerBase
{
    private readonly TemplateDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RequestLogContext _logContext;
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly ILogger<OpenAIController> _logger;
    private readonly MemoryUsageTracker _memoryUsageTracker;



    public OpenAIController(
        TemplateDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        RequestLogContext logContext,
        GlobalSettingsService globalSettingsService,
        ILogger<OpenAIController> logger,
        MemoryUsageTracker memoryUsageTracker)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logContext = logContext;
        _globalSettingsService = globalSettingsService;
        _logger = logger;
        _memoryUsageTracker = memoryUsageTracker;
    }

    private async Task<bool> IsAuthorizedAsync()
    {
        if (User.Identity?.IsAuthenticated == true) return true;

        var result = await HttpContext.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        if (result.Succeeded)
        {
            HttpContext.User = result.Principal;
            return true;
        }

        return await _globalSettingsService.GetAllowAnonymousApiCallAsync();
    }



    [HttpPost("/v1/chat/completions")]
    public async Task Chat()
    {
        if (!await IsAuthorizedAsync())
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized. Please provide a valid Bearer token or enable anonymous access.");
            return;
        }

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
            var modelToUse = string.IsNullOrWhiteSpace(inputModelVal)
                ? await _globalSettingsService.GetDefaultChatModelAsync()
                : inputModelVal;

            var virtualModel = await _dbContext.VirtualModels
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Name == modelToUse && m.Type == ModelType.Chat);

            if (virtualModel == null || virtualModel.Provider == null)
            {
                Response.StatusCode = 404;
                await Response.WriteAsync($"Model '{modelToUse}' not found in gateway or has no provider.");
                return;
            }

            var underlyingUrl = virtualModel.Provider.BaseUrl.TrimEnd('/');

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                _memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            _memoryUsageTracker.TrackUnderlyingModelUsage(virtualModel.Provider.Id, virtualModel.UnderlyingModel);

            _logContext.Log.Model = virtualModel.Name;

            var messagesArray = clientJson["messages"]?.AsArray();
            _logContext.Log.ConversationMessageCount = messagesArray?.Count ?? 0;
            _logContext.Log.LastQuestion = messagesArray?.LastOrDefault()?["content"]?.ToString() ?? string.Empty;

            var isStream = clientJson["stream"]?.GetValue<bool>() ?? false;

            // =========================================================================================
            // 1. 请求翻译阶段：OpenAI 格式 -> Ollama Native 格式
            // =========================================================================================
            var ollamaRequest = new JsonObject
            {
                ["model"] = virtualModel.UnderlyingModel,
                ["stream"] = isStream
            };

            // 【补丁 A：透传工具定义】Ollama 原生支持这部分的 OpenAI 格式
            if (clientJson["tools"] != null) ollamaRequest["tools"] = clientJson["tools"]!.DeepClone();
            if (clientJson["tool_choice"] != null) ollamaRequest["tool_choice"] = clientJson["tool_choice"]!.DeepClone();

            if (messagesArray != null)
            {
                var translatedMessages = new JsonArray();
                foreach (var msgNode in messagesArray)
                {
                    if (msgNode == null) continue;
                    var newMsg = new JsonObject();
                    newMsg["role"] = msgNode["role"]?.ToString();

                    // 处理多模态/复杂数组 Content
                    var contentNode = msgNode["content"];
                    if (contentNode is JsonArray contentArray)
                    {
                        var textBuilder = new StringBuilder();
                        var imagesArray = new JsonArray();

                        foreach (var item in contentArray)
                        {
                            var type = item?["type"]?.ToString();
                            if (type == "text")
                            {
                                textBuilder.Append((string?)item?["text"]);
                            }
                            else if (type == "image_url")
                            {
                                var url = item?["image_url"]?["url"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(url))
                                {
                                    var base64Data = url.Contains(',') ? url.Split(',')[1] : url;
                                    imagesArray.Add(base64Data);
                                }
                            }
                        }
                        newMsg["content"] = textBuilder.ToString();
                        if (imagesArray.Count > 0) newMsg["images"] = imagesArray;
                    }
                    else
                    {
                        newMsg["content"] = contentNode?.ToString() ?? string.Empty;
                    }

                    // 【补丁 B：翻译历史记录中的工具调用】OpenAI 字符串 -> Ollama 对象
                    var tcs = msgNode["tool_calls"]?.AsArray();
                    if (tcs != null && tcs.Count > 0)
                    {
                        var ollamaTcs = new JsonArray();
                        foreach (var tc in tcs)
                        {
                            var oTc = new JsonObject();
                            var funcNode = tc?["function"];
                            if (funcNode != null)
                            {
                                var oFunc = new JsonObject();
                                oFunc["name"] = funcNode["name"]?.ToString();
                                
                                var argsStr = funcNode["arguments"]?.ToString();
                                if (!string.IsNullOrWhiteSpace(argsStr))
                                {
                                    try { oFunc["arguments"] = JsonNode.Parse(argsStr); }
                                    catch { oFunc["arguments"] = new JsonObject(); }
                                }
                                oTc["function"] = oFunc;
                            }
                            ollamaTcs.Add(oTc);
                        }
                        newMsg["tool_calls"] = ollamaTcs;
                    }

                    // 保留 tool_call_id，Ollama 虽不用但透传更安全
                    if (msgNode["tool_call_id"] != null)
                    {
                        newMsg["tool_call_id"] = msgNode["tool_call_id"]?.ToString();
                    }

                    translatedMessages.Add(newMsg);
                }
                
                ollamaRequest["messages"] = translatedMessages;
            }

            var options = new JsonObject();
            if (clientJson["temperature"] != null) options["temperature"] = clientJson["temperature"]!.DeepClone();
            if (clientJson["top_p"] != null) options["top_p"] = clientJson["top_p"]!.DeepClone();
            if (clientJson["max_tokens"] != null) options["num_predict"] = clientJson["max_tokens"]!.DeepClone();

            if (virtualModel.Temperature.HasValue) options["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) options["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.NumPredict.HasValue) options["num_predict"] = virtualModel.NumPredict.Value;
            if (virtualModel.NumCtx.HasValue) options["num_ctx"] = virtualModel.NumCtx.Value;

            if (options.Count > 0) ollamaRequest["options"] = options;
            if (virtualModel.Thinking.HasValue) ollamaRequest["think"] = virtualModel.Thinking.Value;

            // =========================================================================================
            // 2. 发起底层请求
            // =========================================================================================
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();

            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
            {
                Content = new StringContent(ollamaRequest.ToJsonString(), Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("[{TraceId}] Translating & Proxying OpenAI chat request to {UnderlyingUrl}/api/chat", HttpContext.TraceIdentifier, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);

            Response.StatusCode = (int)response.StatusCode;
            _logContext.Log.StatusCode = Response.StatusCode;
            _logContext.Log.Success = response.IsSuccessStatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                _logContext.Log.Answer = errContent;
                Response.ContentType = "application/json";
                await Response.WriteAsync(errContent, HttpContext.RequestAborted);
                return;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
            var chatId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // =========================================================================================
            // 3. 响应翻译阶段 (Streaming)
            // =========================================================================================
            if (isStream)
            {
                Response.ContentType = "text/event-stream";
                
                var answerBuilder = new StringBuilder();
                var thinkBuilder = new StringBuilder();
                using var reader = new StreamReader(responseStream);
                string? line;
                
                while ((line = await reader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    try
                    {
                        var ollamaChunk = JsonNode.Parse(line);
                        if (ollamaChunk == null) continue;

                        var content = ollamaChunk["message"]?["content"]?.ToString() ?? string.Empty;
                        var reasoning = ollamaChunk["message"]?["thinking"]?.ToString() 
                                     ?? ollamaChunk["message"]?["think"]?.ToString() 
                                     ?? string.Empty;
                        var isDone = ollamaChunk["done"]?.GetValue<bool>() ?? false;

                        if (!string.IsNullOrEmpty(content)) answerBuilder.Append(content);
                        if (!string.IsNullOrEmpty(reasoning)) thinkBuilder.Append(reasoning);

                        var openAiChunk = new JsonObject
                        {
                            ["id"] = chatId,
                            ["object"] = "chat.completion.chunk",
                            ["created"] = created,
                            ["model"] = virtualModel.Name,
                            ["choices"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = 0,
                                    ["delta"] = new JsonObject(),
                                    ["finish_reason"] = null
                                }
                            }
                        };

                        var delta = openAiChunk["choices"]![0]!["delta"]!.AsObject();
                        if (!string.IsNullOrEmpty(content)) delta["content"] = content;
                        if (!string.IsNullOrEmpty(reasoning)) delta["reasoning_content"] = reasoning;

                        // 【补丁 C：翻译模型下发的工具调用指令】Ollama 对象 -> OpenAI 字符串
                        var toolCalls = ollamaChunk["message"]?["tool_calls"]?.AsArray();
                        if (toolCalls != null && toolCalls.Count > 0)
                        {
                            var openAiToolCalls = new JsonArray();
                            for (int i = 0; i < toolCalls.Count; i++)
                            {
                                var tc = toolCalls[i];
                                openAiToolCalls.Add(new JsonObject
                                {
                                    ["index"] = i,
                                    ["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = tc?["function"]?["name"]?.ToString(),
                                        ["arguments"] = tc?["function"]?["arguments"]?.ToJsonString() ?? "{}"
                                    }
                                });
                            }
                            delta["tool_calls"] = openAiToolCalls;
                        }

                        // 如果这一行真的是空包（没有文本、没有思考、没有工具、也没有结束标志），则跳过
                        if (delta.Count == 0 && !isDone) continue;

                        if (isDone)
                        {
                            openAiChunk["choices"]![0]!["finish_reason"] = "stop";

                            var pTokens = ollamaChunk["prompt_eval_count"]?.GetValue<long>() ?? 0;
                            var cTokens = ollamaChunk["eval_count"]?.GetValue<long>() ?? 0;
                            if (pTokens > 0 || cTokens > 0)
                            {
                                openAiChunk["usage"] = new JsonObject
                                {
                                    ["prompt_tokens"] = pTokens,
                                    ["completion_tokens"] = cTokens,
                                    ["total_tokens"] = pTokens + cTokens
                                };
                                _logContext.Log.PromptTokens = (int)pTokens;
                                _logContext.Log.CompletionTokens = (int)cTokens;
                                _logContext.Log.TotalTokens = (int)(pTokens + cTokens);
                            }
                        }

                        await Response.WriteAsync($"data: {openAiChunk.ToJsonString()}\n\n", HttpContext.RequestAborted);
                        await Response.Body.FlushAsync(HttpContext.RequestAborted);

                        if (isDone)
                        {
                            await Response.WriteAsync("data: [DONE]\n\n", HttpContext.RequestAborted);
                            await Response.Body.FlushAsync(HttpContext.RequestAborted);
                        }
                    }
                    catch { /* 忽略脏数据 */ }
                }

                _logContext.Log.Answer = answerBuilder.ToString();
                _logContext.Log.Thinking = thinkBuilder.ToString();
            }
            // =========================================================================================
            // 4. 响应翻译阶段 (Non-Streaming)
            // =========================================================================================
            else
            {
                using var ms = new MemoryStream();
                await responseStream.CopyToAsync(ms, HttpContext.RequestAborted);
                ms.Seek(0, SeekOrigin.Begin);

                try
                {
                    var ollamaResponse = await JsonNode.ParseAsync(ms, cancellationToken: HttpContext.RequestAborted);
                    if (ollamaResponse != null)
                    {
                        var content = ollamaResponse["message"]?["content"]?.ToString() ?? string.Empty;
                        var reasoning = ollamaResponse["message"]?["thinking"]?.ToString() 
                                     ?? ollamaResponse["message"]?["think"]?.ToString() 
                                     ?? string.Empty;
                        var pTokens = ollamaResponse["prompt_eval_count"]?.GetValue<long>() ?? 0;
                        var cTokens = ollamaResponse["eval_count"]?.GetValue<long>() ?? 0;

                        _logContext.Log.Answer = content;
                        _logContext.Log.Thinking = reasoning;
                        _logContext.Log.PromptTokens = (int)pTokens;
                        _logContext.Log.CompletionTokens = (int)cTokens;
                        _logContext.Log.TotalTokens = (int)(pTokens + cTokens);

                        var openAiResponse = new JsonObject
                        {
                            ["id"] = chatId,
                            ["object"] = "chat.completion",
                            ["created"] = created,
                            ["model"] = virtualModel.Name,
                            ["choices"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["index"] = 0,
                                    ["message"] = new JsonObject
                                    {
                                        ["role"] = "assistant",
                                        ["content"] = content
                                    },
                                    ["finish_reason"] = "stop"
                                }
                            },
                            ["usage"] = new JsonObject
                            {
                                ["prompt_tokens"] = pTokens,
                                ["completion_tokens"] = cTokens,
                                ["total_tokens"] = pTokens + cTokens
                            }
                        };

                        if (!string.IsNullOrEmpty(reasoning))
                        {
                            openAiResponse["choices"]![0]!["message"]!["reasoning_content"] = reasoning;
                        }

                        // 【补丁 D：翻译非流式模型下发的工具调用指令】
                        var toolCalls = ollamaResponse["message"]?["tool_calls"]?.AsArray();
                        if (toolCalls != null && toolCalls.Count > 0)
                        {
                            var openAiToolCalls = new JsonArray();
                            for (int i = 0; i < toolCalls.Count; i++)
                            {
                                var tc = toolCalls[i];
                                openAiToolCalls.Add(new JsonObject
                                {
                                    ["id"] = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                    ["type"] = "function",
                                    ["function"] = new JsonObject
                                    {
                                        ["name"] = tc?["function"]?["name"]?.ToString(),
                                        ["arguments"] = tc?["function"]?["arguments"]?.ToJsonString() ?? "{}"
                                    }
                                });
                            }
                            openAiResponse["choices"]![0]!["message"]!["tool_calls"] = openAiToolCalls;
                        }

                        Response.ContentType = "application/json";
                        await Response.WriteAsync(openAiResponse.ToJsonString(), HttpContext.RequestAborted);
                    }
                }
                catch
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    using var sReader = new StreamReader(ms, Encoding.UTF8, false, 1024, true);
                    var rawErr = await sReader.ReadToEndAsync(HttpContext.RequestAborted);
                    _logContext.Log.Answer = rawErr;
                    Response.ContentType = "application/json";
                    await Response.WriteAsync(rawErr, HttpContext.RequestAborted);
                }
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
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
    }

    [HttpPost("/v1/embeddings")]
    public async Task Embed()
    {
        if (!await IsAuthorizedAsync())
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized.");
            return;
        }

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
            var modelName = string.IsNullOrWhiteSpace(inputModelVal)
                ? await _globalSettingsService.GetDefaultEmbeddingModelAsync()
                : inputModelVal;

            var virtualModel = await _dbContext.VirtualModels
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Name == modelName && m.Type == ModelType.Embedding);

            if (virtualModel == null || virtualModel.Provider == null)
            {
                Response.StatusCode = 404;
                await Response.WriteAsync($"Embedding model '{modelName}' not found in gateway or has no provider.");
                return;
            }

            var underlyingUrl = virtualModel.Provider.BaseUrl.TrimEnd('/');
            
            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                _memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            _memoryUsageTracker.TrackUnderlyingModelUsage(virtualModel.Provider.Id, virtualModel.UnderlyingModel);
            
            _logContext.Log.Model = virtualModel.Name;
            _logContext.Log.ConversationMessageCount = 1;
            _logContext.Log.LastQuestion = clientJson["input"]?.ToString() ?? string.Empty;

            // =========================================================================================
            // 1. 请求翻译阶段：OpenAI 格式 (/v1/embeddings) -> Ollama Native 格式 (/api/embed)
            // =========================================================================================
            var ollamaRequest = new JsonObject
            {
                ["model"] = virtualModel.UnderlyingModel,
                ["input"] = clientJson["input"]!.DeepClone()
            };

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embed")
            {
                Content = new StringContent(ollamaRequest.ToJsonString(), Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("[{TraceId}] Translating & Proxying OpenAI embedding request for model {Model} to {UnderlyingUrl}/api/embed", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            Response.StatusCode = (int)response.StatusCode;
            _logContext.Log.StatusCode = Response.StatusCode;
            _logContext.Log.Success = response.IsSuccessStatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                _logContext.Log.Answer = content;
                Response.ContentType = "application/json";
                await Response.WriteAsync(content, HttpContext.RequestAborted);
                return;
            }

            // =========================================================================================
            // 2. 响应翻译阶段 (Non-Streaming)：Ollama JSON -> OpenAI JSON
            // =========================================================================================
            var responseContent = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
            try
            {
                var ollamaResponse = JsonNode.Parse(responseContent);
                if (ollamaResponse != null)
                {
                    var embeddings = ollamaResponse["embeddings"]?.AsArray();
                    var openAiData = new JsonArray();
                    if (embeddings != null)
                    {
                        for (int i = 0; i < embeddings.Count; i++)
                        {
                            openAiData.Add(new JsonObject
                            {
                                ["object"] = "embedding",
                                ["index"] = i,
                                ["embedding"] = embeddings[i]!.DeepClone()
                            });
                        }
                    }

                    var pTokens = ollamaResponse["prompt_eval_count"]?.GetValue<long>() ?? 0;
                    _logContext.Log.PromptTokens = (int)pTokens;
                    _logContext.Log.TotalTokens = (int)pTokens;

                    var openAiResponse = new JsonObject
                    {
                        ["object"] = "list",
                        ["data"] = openAiData,
                        ["model"] = virtualModel.Name,
                        ["usage"] = new JsonObject
                        {
                            ["prompt_tokens"] = pTokens,
                            ["total_tokens"] = pTokens
                        }
                    };

                    Response.ContentType = "application/json"; // 强制接管 Content-Type
                    await Response.WriteAsync(openAiResponse.ToJsonString(), HttpContext.RequestAborted);
                }
                else
                {
                    await Response.WriteAsync(responseContent, HttpContext.RequestAborted);
                }
            }
            catch
            {
                await Response.WriteAsync(responseContent, HttpContext.RequestAborted);
            }
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
    }

    [HttpGet("/v1/models")]
    public async Task<IActionResult> Models()
    {
        if (!await IsAuthorizedAsync())
        {
            return Unauthorized();
        }

        var virtualModels = await _dbContext.VirtualModels.ToListAsync();
        
        var data = virtualModels.Select(vm => new
        {
            id = vm.Name,
            @object = "model",
            created = ((DateTimeOffset)vm.CreatedAt).ToUnixTimeSeconds(),
            owned_by = "library"
        }).ToList();

        return Ok(new { @object = "list", data });
    }
}
