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

    private static readonly HashSet<string> HeaderBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Upgrade", "Host", "Accept-Ranges"
    };

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
            var jsonNode = JsonNode.Parse(bodyStr)?.AsObject();
            if (jsonNode == null)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Invalid JSON body.");
                return;
            }

            var inputModelVal = jsonNode["model"]?.ToString() ?? string.Empty;
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
            
            var messagesArray = jsonNode["messages"]?.AsArray();
            _logContext.Log.ConversationMessageCount = messagesArray?.Count ?? 0;
            _logContext.Log.LastQuestion = messagesArray?.LastOrDefault()?["content"]?.ToString() ?? string.Empty;

            jsonNode["model"] = virtualModel.UnderlyingModel;
            
            // Inject overrides dynamically into OpenCV payload root structure
            if (virtualModel.Temperature.HasValue) jsonNode["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) jsonNode["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.NumPredict.HasValue) jsonNode["max_tokens"] = virtualModel.NumPredict.Value; 
            if (virtualModel.NumCtx.HasValue)
            {
                var options = jsonNode["options"] ??= new JsonObject();
                options["num_ctx"] = virtualModel.NumCtx.Value;
            }
            if (virtualModel.Thinking.HasValue) jsonNode["think"] = virtualModel.Thinking.Value;

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/v1/chat/completions")
            {
                Content = new StringContent(jsonNode.ToJsonString(), Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("[{TraceId}] Proxying OpenAI chat request for model {Model} to {UnderlyingUrl}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response);

            _logContext.Log.StatusCode = Response.StatusCode;
            _logContext.Log.Success = response.IsSuccessStatusCode;
            
            var isStream = false;
            if (jsonNode["stream"] != null && bool.TryParse(jsonNode["stream"]?.ToString(), out var parsedStream))
                isStream = parsedStream;

            await using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
            
            if (isStream && response.IsSuccessStatusCode)
            {
                var answerBuilder = new StringBuilder();
                var thinkBuilder = new StringBuilder();
                using var reader = new StreamReader(responseStream);
                string? line;
                while ((line = await reader.ReadLineAsync(HttpContext.RequestAborted)) != null)
                {
                    if (line.StartsWith("data: ") && line != "data: [DONE]")
                    {
                        try
                        {
                            var jsonStr = line.Substring(6);
                            var chunkNode = JsonNode.Parse(jsonStr);
                            if (chunkNode != null)
                            {
                                // Mask the physical model name with the virtual one
                                chunkNode["model"] = virtualModel.Name;

                                // Extract audit values
                                var content = chunkNode["choices"]?[0]?["delta"]?["content"]?.ToString();
                                if (!string.IsNullOrEmpty(content)) answerBuilder.Append(content);

                                var reasoningContent = chunkNode["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString();
                                if (string.IsNullOrEmpty(reasoningContent)) 
                                {
                                    reasoningContent = chunkNode["choices"]?[0]?["delta"]?["think"]?.ToString(); 
                                }
                                if (!string.IsNullOrEmpty(reasoningContent)) thinkBuilder.Append(reasoningContent);

                                var usageNode = chunkNode["usage"];
                                if (usageNode != null)
                                {
                                    if (int.TryParse(usageNode["prompt_tokens"]?.ToString(), out var pTokens)) _logContext.Log.PromptTokens = pTokens;
                                    if (int.TryParse(usageNode["completion_tokens"]?.ToString(), out var cTokens)) _logContext.Log.CompletionTokens = cTokens;
                                    _logContext.Log.TotalTokens = _logContext.Log.PromptTokens + _logContext.Log.CompletionTokens;
                                }

                                // Serialize modified JSON, prepend prefix, and send
                                var modifiedLine = $"data: {chunkNode.ToJsonString()}";
                                await Response.WriteAsync(modifiedLine + "\n", HttpContext.RequestAborted);
                                await Response.Body.FlushAsync(HttpContext.RequestAborted);
                                continue;
                            }
                        }
                        catch { /* Fallback to raw output on parse failure */ }
                    }

                    // Fallback: empty lines, [DONE], or parse failures are forwarded as-is
                    await Response.WriteAsync(line + "\n", HttpContext.RequestAborted);
                    await Response.Body.FlushAsync(HttpContext.RequestAborted);
                }
                
                _logContext.Log.Answer = answerBuilder.ToString();
                _logContext.Log.Thinking = thinkBuilder.ToString();
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

                        _logContext.Log.Answer = resultNode["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
                        
                        var reasoning = resultNode["choices"]?[0]?["message"]?["reasoning_content"]?.ToString();
                        if (string.IsNullOrEmpty(reasoning)) 
                        {
                            reasoning = resultNode["choices"]?[0]?["message"]?["think"]?.ToString();
                        }
                        _logContext.Log.Thinking = reasoning ?? string.Empty;

                        var usageNode = resultNode["usage"];
                        if (usageNode != null)
                        {
                            if (int.TryParse(usageNode["prompt_tokens"]?.ToString(), out var pTokens)) _logContext.Log.PromptTokens = pTokens;
                            if (int.TryParse(usageNode["completion_tokens"]?.ToString(), out var cTokens)) _logContext.Log.CompletionTokens = cTokens;
                            _logContext.Log.TotalTokens = _logContext.Log.PromptTokens + _logContext.Log.CompletionTokens;
                        }

                        // Write the modified JSON to the response
                        await Response.WriteAsync(resultNode.ToJsonString(), HttpContext.RequestAborted);
                        contentReplaced = true;
                    }
                }
                catch { /* ignored */ }

                // Only fallback to raw stream if parsing failed or upstream error
                if (!contentReplaced)
                {
                    if (!response.IsSuccessStatusCode && string.IsNullOrWhiteSpace(_logContext.Log.Answer))
                    {
                        ms.Seek(0, SeekOrigin.Begin);
                        using var sReader = new StreamReader(ms, Encoding.UTF8, false, 1024, true);
                        _logContext.Log.Answer = await sReader.ReadToEndAsync(HttpContext.RequestAborted);
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    await ms.CopyToAsync(Response.Body, HttpContext.RequestAborted);
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
            var jsonNode = JsonNode.Parse(bodyStr)?.AsObject();
            if (jsonNode == null)
            {
                Response.StatusCode = 400;
                await Response.WriteAsync("Invalid JSON body.");
                return;
            }

            var inputModelVal = jsonNode["model"]?.ToString() ?? string.Empty;
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
            _logContext.Log.LastQuestion = jsonNode["input"]?.ToString() ?? string.Empty;

            jsonNode["model"] = virtualModel.UnderlyingModel;

            using var client = _httpClientFactory.CreateClient();
            client.Timeout = await _globalSettingsService.GetRequestTimeoutAsync();
            
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/v1/embeddings")
            {
                Content = new StringContent(jsonNode.ToJsonString(), Encoding.UTF8, "application/json")
            };

            _logger.LogInformation("[{TraceId}] Proxying OpenAI embedding request for model {Model} to {UnderlyingUrl}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response);

            _logContext.Log.StatusCode = Response.StatusCode;
            _logContext.Log.Success = response.IsSuccessStatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                _logContext.Log.Answer = content;
                await Response.WriteAsync(content, HttpContext.RequestAborted);
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                try
                {
                    var resultNode = JsonNode.Parse(responseContent);
                    if (resultNode != null)
                    {
                        // Mask physical model name
                        resultNode["model"] = virtualModel.Name;
                        await Response.WriteAsync(resultNode.ToJsonString(), HttpContext.RequestAborted);
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
