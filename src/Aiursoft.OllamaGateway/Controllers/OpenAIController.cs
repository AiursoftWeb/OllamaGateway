using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RequestLogContext _logContext;
    private readonly GlobalSettingsService _globalSettingsService;
    private readonly ILogger<OpenAIController> _logger;
    private readonly MemoryUsageTracker _memoryUsageTracker;
    private readonly IModelSelector _modelSelector;
    private readonly ActiveRequestTracker _activeRequestTracker;



    public OpenAIController(
        TemplateDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        RequestLogContext logContext,
        GlobalSettingsService globalSettingsService,
        ILogger<OpenAIController> logger,
        MemoryUsageTracker memoryUsageTracker,
        IModelSelector modelSelector,
        ActiveRequestTracker activeRequestTracker)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logContext = logContext;
        _globalSettingsService = globalSettingsService;
        _logger = logger;
        _memoryUsageTracker = memoryUsageTracker;
        _modelSelector = modelSelector;
        _activeRequestTracker = activeRequestTracker;
    }



    [HttpPost("/v1/chat/completions")]
    public async Task Chat()
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
            var modelToUse = string.IsNullOrWhiteSpace(inputModelVal)
                ? await _globalSettingsService.GetDefaultChatModelAsync()
                : inputModelVal;

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

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');

            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                _memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);

            _logContext.Log.Model = virtualModel.Name;

            var messagesArray = clientJson["messages"]?.AsArray();
            _logContext.Log.ConversationMessageCount = messagesArray?.Count ?? 0;
            _logContext.Log.LastQuestion = messagesArray?.LastOrDefault()?["content"]?.ToString() ?? string.Empty;
            _activeRequestTracker.StartRequest(virtualModel.Name, _logContext.Log.LastQuestion, backend.UnderlyingModelName);

            var isStream = clientJson["stream"]?.GetValue<bool>() ?? false;

            // =========================================================================================
            // OpenAI-compatible Backend Path: direct passthrough, no format translation needed
            // =========================================================================================
            if (backend.Provider.ProviderType == ProviderType.OpenAI)
            {
                // Replace model name; apply VirtualModel parameter overrides on top of client request
                clientJson["model"] = backend.UnderlyingModelName;
                if (virtualModel.Temperature.HasValue) clientJson["temperature"] = virtualModel.Temperature.Value;
                if (virtualModel.TopP.HasValue) clientJson["top_p"] = virtualModel.TopP.Value;
                if (virtualModel.NumPredict.HasValue) clientJson["max_tokens"] = virtualModel.NumPredict.Value;
                if (isStream) clientJson["stream_options"] = new JsonObject { ["include_usage"] = true };

                HttpResponseMessage? oaiDirectResponse = null;
                for (var i = 0; i < virtualModel.MaxRetries; i++)
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
                    var oaiDirectReq = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/v1/chat/completions")
                    {
                        Content = new StringContent(clientJson.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                        oaiDirectReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);

                    using var ctsDirect = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                    ctsDirect.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                    _logger.LogInformation("[{TraceId}] Pass-through OpenAI chat for model {Model} to {UnderlyingUrl}/v1/chat/completions, attempt {Attempt}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);

                    try
                    {
                        oaiDirectResponse = await client.SendAsync(oaiDirectReq, HttpCompletionOption.ResponseHeadersRead, ctsDirect.Token);
                        if (oaiDirectResponse.IsSuccessStatusCode) { _modelSelector.ReportSuccess(backend.Id); _logContext.Log.BackendId = backend.Id; break; }
                        if ((int)oaiDirectResponse.StatusCode >= 500 && !Response.HasStarted)
                            throw new HttpRequestException($"Received {oaiDirectResponse.StatusCode} from upstream OpenAI provider.");
                        _modelSelector.ReportSuccess(backend.Id); _logContext.Log.BackendId = backend.Id; break;
                    }
                    catch (Exception ex) when (!Response.HasStarted)
                    {
                        _modelSelector.ReportFailure(backend.Id);
                        _logger.LogWarning(ex, "Attempt {Attempt} failed for OpenAI direct passthrough to {UnderlyingUrl}", i + 1, underlyingUrl);
                        if (i == virtualModel.MaxRetries - 1) throw;
                        backend = _modelSelector.SelectBackend(virtualModel);
                        if (backend?.Provider == null) { Response.StatusCode = 503; await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'."); return; }
                        underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
                        clientJson["model"] = backend.UnderlyingModelName;
                        _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                    }
                }

                if (oaiDirectResponse == null) return;

                Response.StatusCode = (int)oaiDirectResponse.StatusCode;
                _logContext.Log.StatusCode = Response.StatusCode;
                _logContext.Log.Success = oaiDirectResponse.IsSuccessStatusCode;

                if (!oaiDirectResponse.IsSuccessStatusCode)
                {
                    var errContent = await oaiDirectResponse.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    _logContext.Log.Answer = errContent;
                    Response.ContentType = "application/json";
                    await Response.WriteAsync(errContent, HttpContext.RequestAborted);
                    return;
                }

                await using var directStream = await oaiDirectResponse.Content.ReadAsStreamAsync(HttpContext.RequestAborted);

                if (isStream)
                {
                    Response.ContentType = "text/event-stream";
                    var answerBuilderDirect = new StringBuilder();
                    var thinkBuilderDirect = new StringBuilder();
                    using var directReader = new StreamReader(directStream);
                    string? sLine;
                    while ((sLine = await directReader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                    {
                        if (string.IsNullOrWhiteSpace(sLine)) continue;
                        if (sLine.StartsWith("data: ") && sLine != "data: [DONE]")
                        {
                            try
                            {
                                var chunk = JsonNode.Parse(sLine["data: ".Length..]);
                                if (chunk != null)
                                {
                                    chunk["model"] = virtualModel.Name;
                                    var deltaContent = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                                    var deltaReasoning = chunk["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString();
                                    if (!string.IsNullOrEmpty(deltaContent)) answerBuilderDirect.Append(deltaContent);
                                    if (!string.IsNullOrEmpty(deltaReasoning)) thinkBuilderDirect.Append(deltaReasoning);
                                    if (chunk["usage"] != null)
                                    {
                                        var pTok = chunk["usage"]!["prompt_tokens"]?.GetValue<long>() ?? 0;
                                        var cTok = chunk["usage"]!["completion_tokens"]?.GetValue<long>() ?? 0;
                                        _logContext.Log.PromptTokens = (int)pTok;
                                        _logContext.Log.CompletionTokens = (int)cTok;
                                        _logContext.Log.TotalTokens = (int)(pTok + cTok);
                                    }
                                    await Response.WriteAsync($"data: {chunk.ToJsonString()}\n\n", HttpContext.RequestAborted);
                                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                                    continue;
                                }
                            }
                            catch { /* fall through to raw write */ }
                        }
                        if (sLine == "data: [DONE]")
                        {
                            await Response.WriteAsync("data: [DONE]\n\n", HttpContext.RequestAborted);
                            await Response.Body.FlushAsync(HttpContext.RequestAborted);
                        }
                        else if (!string.IsNullOrWhiteSpace(sLine))
                        {
                            await Response.WriteAsync(sLine + "\n", HttpContext.RequestAborted);
                            await Response.Body.FlushAsync(HttpContext.RequestAborted);
                        }
                    }
                    _logContext.Log.Answer = answerBuilderDirect.ToString();
                    _logContext.Log.Thinking = thinkBuilderDirect.ToString();
                }
                else
                {
                    using var directMs = new MemoryStream();
                    await directStream.CopyToAsync(directMs, HttpContext.RequestAborted);
                    directMs.Seek(0, SeekOrigin.Begin);
                    try
                    {
                        var respNode = await JsonNode.ParseAsync(directMs, cancellationToken: HttpContext.RequestAborted);
                        if (respNode != null)
                        {
                            respNode["model"] = virtualModel.Name;
                            var respContent = respNode["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
                            var respReasoning = respNode["choices"]?[0]?["message"]?["reasoning_content"]?.ToString() ?? string.Empty;
                            var pTok = respNode["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
                            var cTok = respNode["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;
                            _logContext.Log.Answer = respContent;
                            _logContext.Log.Thinking = respReasoning;
                            _logContext.Log.PromptTokens = (int)pTok;
                            _logContext.Log.CompletionTokens = (int)cTok;
                            _logContext.Log.TotalTokens = (int)(pTok + cTok);
                            Response.ContentType = "application/json";
                            await Response.WriteAsync(respNode.ToJsonString(), HttpContext.RequestAborted);
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
                return;
            }
            // =========================================================================================
            // End OpenAI Direct Passthrough — format translation path continues below
            // =========================================================================================

            // =========================================================================================
            // 1. 请求翻译阶段：OpenAI 格式 -> Ollama Native 格式
            // =========================================================================================
            var ollamaRequest = new JsonObject
            {
                ["model"] = backend.UnderlyingModelName,
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
                                var textVal = item?["text"]?.ToString();
                                if (!string.IsNullOrEmpty(textVal))
                                {
                                    textBuilder.Append(textVal);
                                }
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
                            if (tc?["id"] != null) oTc["id"] = tc["id"]!.DeepClone();
                            if (tc?["type"] != null) oTc["type"] = tc["type"]!.DeepClone();

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
            if (virtualModel.TopK.HasValue) options["top_k"] = virtualModel.TopK.Value;
            if (virtualModel.NumPredict.HasValue) options["num_predict"] = virtualModel.NumPredict.Value;
            if (virtualModel.NumCtx.HasValue) options["num_ctx"] = virtualModel.NumCtx.Value;

            if (options.Count > 0) ollamaRequest["options"] = options;
            if (virtualModel.Thinking.HasValue) ollamaRequest["think"] = virtualModel.Thinking.Value;

            // =========================================================================================
            // 2. 发起底层请求
            // =========================================================================================
            HttpResponseMessage? response = null;
            for (var i = 0; i < virtualModel.MaxRetries; i++)
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();

                var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
                {
                    Content = new StringContent(ollamaRequest.ToJsonString(), Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                _logger.LogInformation("[{TraceId}] Translating & Proxying OpenAI chat request to {UnderlyingUrl}/api/chat, attempt {Attempt}", HttpContext.TraceIdentifier, underlyingUrl, i + 1);
                
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        _modelSelector.ReportSuccess(backend.Id);
                        _logContext.Log.BackendId = backend.Id;
                        break;
                    }
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500 && !Response.HasStarted)
                    {
                        throw new HttpRequestException($"Received {response.StatusCode} from upstream.");
                    }
                    else
                    {
                        _modelSelector.ReportSuccess(backend.Id);
                        _logContext.Log.BackendId = backend.Id;
                        break;
                    }
                }
                catch (Exception ex) when (!Response.HasStarted)
                {
                    _modelSelector.ReportFailure(backend.Id);
                    _logger.LogWarning(ex, "Attempt {Attempt} failed for OpenAI chat request to {UnderlyingUrl}", i + 1, underlyingUrl);
                    if (i == virtualModel.MaxRetries - 1)
                    {
                        throw;
                    }
                    
                    backend = _modelSelector.SelectBackend(virtualModel);
                    if (backend == null)
                    {
                        Response.StatusCode = 503;
                        await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                        return;
                    }
                    underlyingUrl = backend.Provider!.BaseUrl.TrimEnd('/');
                    ollamaRequest["model"] = backend.UnderlyingModelName;
                    _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                }
            }
            
            if (response == null) return;

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
                bool isFirstChunk = true;
                bool streamHasToolCalls = false;
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
                        if (isFirstChunk)
                        {
                            delta["role"] = "assistant";
                            isFirstChunk = false;
                        }
                        if (!string.IsNullOrEmpty(content)) delta["content"] = content;
                        if (!string.IsNullOrEmpty(reasoning)) delta["reasoning_content"] = reasoning;

                        // 【补丁 C：翻译模型下发的工具调用指令】Ollama 对象 -> OpenAI 字符串
                        var toolCalls = ollamaChunk["message"]?["tool_calls"]?.AsArray();
                        if (toolCalls != null && toolCalls.Count > 0)
                        {
                            streamHasToolCalls = true;
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
                            openAiChunk["choices"]![0]!["finish_reason"] = streamHasToolCalls ? "tool_calls" : "stop";

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

                        // 【补丁 D：翻译非流式模型下发的工具调用指令】
                        var toolCalls = ollamaResponse["message"]?["tool_calls"]?.AsArray();
                        var hasToolCalls = toolCalls != null && toolCalls.Count > 0;

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
                                    ["finish_reason"] = hasToolCalls ? "tool_calls" : "stop"
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
        finally
        {
            if (!string.IsNullOrEmpty(_logContext.Log.Model))
                _activeRequestTracker.EndRequest(_logContext.Log.Model);
        }
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
            var modelName = string.IsNullOrWhiteSpace(inputModelVal)
                ? await _globalSettingsService.GetDefaultEmbeddingModelAsync()
                : inputModelVal;

            var virtualModel = await _dbContext.VirtualModels
                .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                .FirstOrDefaultAsync(m => m.Name == modelName && m.Type == ModelType.Embedding);

            if (virtualModel == null)
            {
                Response.StatusCode = 404;
                await Response.WriteAsync($"Embedding model '{modelName}' not found in gateway.");
                return;
            }

            var backend = _modelSelector.SelectBackend(virtualModel);
            if (backend == null || backend.Provider == null)
            {
                Response.StatusCode = 503;
                await Response.WriteAsync($"No available backend for model '{modelName}'.");
                return;
            }

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
            
            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                _memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            
            _logContext.Log.Model = virtualModel.Name;
            _logContext.Log.ConversationMessageCount = 1;
            _logContext.Log.LastQuestion = clientJson["input"]?.ToString() ?? string.Empty;

            // =========================================================================================
            // OpenAI-compatible Backend Path: direct passthrough for embeddings
            // =========================================================================================
            if (backend.Provider.ProviderType == ProviderType.OpenAI)
            {
                clientJson["model"] = backend.UnderlyingModelName;

                HttpResponseMessage? oaiEmbedDirect = null;
                for (var i = 0; i < virtualModel.MaxRetries; i++)
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
                    var oaiEmbedDirectReq = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/v1/embeddings")
                    {
                        Content = new StringContent(clientJson.ToJsonString(), Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                        oaiEmbedDirectReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);

                    using var ctsEmbedDirect = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                    ctsEmbedDirect.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                    _logger.LogInformation("[{TraceId}] Pass-through OpenAI embedding for model {Model} to {UnderlyingUrl}/v1/embeddings, attempt {Attempt}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);

                    try
                    {
                        oaiEmbedDirect = await client.SendAsync(oaiEmbedDirectReq, HttpCompletionOption.ResponseHeadersRead, ctsEmbedDirect.Token);
                        if (oaiEmbedDirect.IsSuccessStatusCode) { _modelSelector.ReportSuccess(backend.Id); _logContext.Log.BackendId = backend.Id; break; }
                        if ((int)oaiEmbedDirect.StatusCode >= 500 && !Response.HasStarted)
                            throw new HttpRequestException($"Received {oaiEmbedDirect.StatusCode} from upstream OpenAI embedding provider.");
                        _modelSelector.ReportSuccess(backend.Id); _logContext.Log.BackendId = backend.Id; break;
                    }
                    catch (Exception ex) when (!Response.HasStarted)
                    {
                        _modelSelector.ReportFailure(backend.Id);
                        _logger.LogWarning(ex, "Attempt {Attempt} failed for OpenAI embedding direct passthrough to {UnderlyingUrl}", i + 1, underlyingUrl);
                        if (i == virtualModel.MaxRetries - 1) throw;
                        backend = _modelSelector.SelectBackend(virtualModel);
                        if (backend?.Provider == null) { Response.StatusCode = 503; await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'."); return; }
                        underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
                        clientJson["model"] = backend.UnderlyingModelName;
                        _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                    }
                }

                if (oaiEmbedDirect == null) return;

                Response.StatusCode = (int)oaiEmbedDirect.StatusCode;
                _logContext.Log.StatusCode = Response.StatusCode;
                _logContext.Log.Success = oaiEmbedDirect.IsSuccessStatusCode;

                if (!oaiEmbedDirect.IsSuccessStatusCode)
                {
                    var errContent = await oaiEmbedDirect.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                    _logContext.Log.Answer = errContent;
                    Response.ContentType = "application/json";
                    await Response.WriteAsync(errContent, HttpContext.RequestAborted);
                    return;
                }

                var embedRespContent = await oaiEmbedDirect.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                try
                {
                    var embedRespNode = JsonNode.Parse(embedRespContent);
                    if (embedRespNode != null)
                    {
                        embedRespNode["model"] = virtualModel.Name;
                        var pTokens = embedRespNode["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
                        _logContext.Log.PromptTokens = (int)pTokens;
                        _logContext.Log.TotalTokens = (int)pTokens;
                        Response.ContentType = "application/json";
                        await Response.WriteAsync(embedRespNode.ToJsonString(), HttpContext.RequestAborted);
                        return;
                    }
                }
                catch { /* fall through */ }
                await Response.WriteAsync(embedRespContent, HttpContext.RequestAborted);
                return;
            }
            // =========================================================================================
            // End OpenAI Embedding Direct Passthrough — format translation path continues below
            // =========================================================================================

            // =========================================================================================
            // 1. 请求翻译阶段：OpenAI 格式 (/v1/embeddings) -> Ollama Native 格式 (/api/embed)
            // =========================================================================================
            var ollamaRequest = new JsonObject
            {
                ["model"] = backend.UnderlyingModelName,
                ["input"] = clientJson["input"]!.DeepClone()
            };

            var options = new JsonObject();
            if (virtualModel.NumCtx.HasValue) options["num_ctx"] = virtualModel.NumCtx.Value;
            if (virtualModel.Temperature.HasValue) options["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) options["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.TopK.HasValue) options["top_k"] = virtualModel.TopK.Value;
            if (virtualModel.RepeatPenalty.HasValue) options["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
            if (options.Count > 0) ollamaRequest["options"] = options;

            HttpResponseMessage? response = null;
            for (var i = 0; i < virtualModel.MaxRetries; i++)
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
                
                var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embed")
                {
                    Content = new StringContent(ollamaRequest.ToJsonString(), Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                _logger.LogInformation("[{TraceId}] Translating & Proxying OpenAI embedding request for model {Model} to {UnderlyingUrl}/api/embed, attempt {Attempt}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);
                
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        _modelSelector.ReportSuccess(backend.Id);
                        _logContext.Log.BackendId = backend.Id;
                        break;
                    }
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500 && !Response.HasStarted)
                    {
                        throw new HttpRequestException($"Received {response.StatusCode} from upstream.");
                    }
                    else
                    {
                        _modelSelector.ReportSuccess(backend.Id);
                        _logContext.Log.BackendId = backend.Id;
                        break;
                    }
                }
                catch (Exception ex) when (!Response.HasStarted)
                {
                    _modelSelector.ReportFailure(backend.Id);
                    _logger.LogWarning(ex, "Attempt {Attempt} failed for OpenAI embedding request to {UnderlyingUrl}", i + 1, underlyingUrl);
                    if (i == virtualModel.MaxRetries - 1)
                    {
                        throw;
                    }
                    
                    backend = _modelSelector.SelectBackend(virtualModel);
                    if (backend == null)
                    {
                        Response.StatusCode = 503;
                        await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                        return;
                    }
                    underlyingUrl = backend.Provider!.BaseUrl.TrimEnd('/');
                    ollamaRequest["model"] = backend.UnderlyingModelName;
                    _memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                }
            }

            if (response == null) return;
            
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
}
