using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.GptClient.Abstractions;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Controllers;

[Route("api")]
[AllowAnonymous]
public class ProxyController(
    TemplateDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    RequestLogContext logContext,
    OllamaService ollamaService,
    GlobalSettingsService globalSettingsService,
    ILogger<ProxyController> logger,
    MemoryUsageTracker memoryUsageTracker,
    IModelSelector modelSelector) : ControllerBase
{
    private static readonly HashSet<string> HeaderBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding", "Content-Length", "Connection", "Keep-Alive", "Upgrade", "Host", "Accept-Ranges"
    };

    private async Task<bool> IsAuthorizedAsync()
    {
        // 1. If already authenticated by cookie or other middleware
        if (User.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        // 2. Manually trigger ApiKey authentication because [AllowAnonymous] bypasses the automatic challenge
        var result = await HttpContext.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        if (result.Succeeded)
        {
            // Update the User property so subsequent code can access claims
            HttpContext.User = result.Principal;
            return true;
        }

        // 3. Check global setting for anonymous access
        return await globalSettingsService.GetAllowAnonymousApiCallAsync();
    }

    [HttpPost("chat")]
    public async Task Chat([FromBody] OllamaRequestModel input)
    {
        if (!await IsAuthorizedAsync())
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized. Please provide a valid Bearer token or enable anonymous access.");
            return;
        }

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
                        HealthCheckTimeout = 30,
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

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
            
            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            
            logContext.Log.Model = virtualModel.Name;
            logContext.Log.ConversationMessageCount = input.Messages.Count;
            logContext.Log.LastQuestion = input.Messages.LastOrDefault()?.Content ?? string.Empty;

            input.Model = backend.UnderlyingModelName;
            if (virtualModel.Thinking.HasValue) input.Think = virtualModel.Thinking.Value;
            
            input.Options ??= new OllamaRequestOptions();
            if (virtualModel.NumCtx.HasValue) input.Options.NumCtx = virtualModel.NumCtx;
            if (virtualModel.Temperature.HasValue) input.Options.Temperature = virtualModel.Temperature;
            if (virtualModel.TopP.HasValue) input.Options.TopP = virtualModel.TopP;
            if (virtualModel.TopK.HasValue) input.Options.TopK = virtualModel.TopK;
            if (virtualModel.NumPredict.HasValue) input.Options.NumPredict = virtualModel.NumPredict;
            if (virtualModel.RepeatPenalty.HasValue) input.Options.RepeatPenalty = virtualModel.RepeatPenalty;

            HttpResponseMessage? response = null;
            for (var i = 0; i < virtualModel.MaxRetries; i++)
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
                if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
                }
                
                var json = System.Text.Json.JsonSerializer.Serialize(input, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                logger.LogInformation("[{TraceId}] Proxying chat request for model {Model} to {UnderlyingUrl}, attempt {Attempt}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);
                
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        modelSelector.ReportSuccess(backend.Id);
                        logContext.Log.BackendId = backend.Id;
                        break;
                    }
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500 && !Response.HasStarted)
                    {
                        throw new HttpRequestException($"Received {response.StatusCode} from upstream.");
                    }
                    else
                    {
                        modelSelector.ReportSuccess(backend.Id);
                        logContext.Log.BackendId = backend.Id;
                        break;
                    }
                }
                catch (Exception ex) when (!Response.HasStarted)
                {
                    modelSelector.ReportFailure(backend.Id);
                    logger.LogWarning(ex, "Attempt {Attempt} failed for model {Model} to {UnderlyingUrl}", i + 1, virtualModel.Name, underlyingUrl);
                    if (i == virtualModel.MaxRetries - 1)
                    {
                        throw;
                    }
                    
                    backend = modelSelector.SelectBackend(virtualModel);
                    if (backend == null || backend.Provider == null)
                    {
                        Response.StatusCode = 503;
                        await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                        return;
                    }
                    underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
                    input.Model = backend.UnderlyingModelName;
                    memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                }
            }
            
            if (response == null) return;
            
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
    }

    [HttpPost("embed")]
    public async Task Embed([FromBody] dynamic input)
    {
        if (!await IsAuthorizedAsync())
        {
            Response.StatusCode = 401;
            await Response.WriteAsync("Unauthorized.");
            return;
        }

        if (input == null)
        {
            Response.StatusCode = 400;
            await Response.WriteAsync("Request body is empty or invalid JSON.");
            return;
        }

        logContext.Log.UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous";
        logContext.Log.ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous");

        try
        {
            string modelName = input.model;
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

            var underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
            
            var apiKeyIdClaim = User.FindFirst("ApiKeyId");
            if (apiKeyIdClaim != null && int.TryParse(apiKeyIdClaim.Value, out var apiKeyId))
            {
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
            
            logContext.Log.Model = virtualModel.Name;
            logContext.Log.ConversationMessageCount = 1;
            logContext.Log.LastQuestion = input.input?.ToString() ?? input.prompt?.ToString() ?? string.Empty;

            input.model = backend.UnderlyingModelName;

            if (virtualModel.NumCtx.HasValue || virtualModel.Temperature.HasValue || virtualModel.TopP.HasValue || virtualModel.TopK.HasValue || virtualModel.RepeatPenalty.HasValue)
            {
                input.options ??= new System.Dynamic.ExpandoObject();
                if (virtualModel.NumCtx.HasValue) input.options.num_ctx = virtualModel.NumCtx.Value;
                if (virtualModel.Temperature.HasValue) input.options.temperature = virtualModel.Temperature.Value;
                if (virtualModel.TopP.HasValue) input.options.top_p = virtualModel.TopP.Value;
                if (virtualModel.TopK.HasValue) input.options.top_k = virtualModel.TopK.Value;
                if (virtualModel.RepeatPenalty.HasValue) input.options.repeat_penalty = virtualModel.RepeatPenalty.Value;
            }

            HttpResponseMessage? response = null;
            for (var i = 0; i < virtualModel.MaxRetries; i++)
            {
                var client = httpClientFactory.CreateClient();
                client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
                if (!string.IsNullOrWhiteSpace(backend.Provider.BearerToken))
                {
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", backend.Provider.BearerToken);
                }
                
                var json = System.Text.Json.JsonSerializer.Serialize((object)input, new System.Text.Json.JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embed")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(virtualModel.HealthCheckTimeout));

                logger.LogInformation("[{TraceId}] Proxying embedding request for model {Model} to {UnderlyingUrl}, attempt {Attempt}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl, i + 1);
                
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        modelSelector.ReportSuccess(backend.Id);
                        logContext.Log.BackendId = backend.Id;
                        break;
                    }
                    if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500 && !Response.HasStarted)
                    {
                        throw new HttpRequestException($"Received {response.StatusCode} from upstream.");
                    }
                    else
                    {
                        modelSelector.ReportSuccess(backend.Id);
                        logContext.Log.BackendId = backend.Id;
                        break;
                    }
                }
                catch (Exception ex) when (!Response.HasStarted)
                {
                    modelSelector.ReportFailure(backend.Id);
                    logger.LogWarning(ex, "Attempt {Attempt} failed for embedding model {Model} to {UnderlyingUrl}", i + 1, virtualModel.Name, underlyingUrl);
                    if (i == virtualModel.MaxRetries - 1)
                    {
                        throw;
                    }
                    
                    backend = modelSelector.SelectBackend(virtualModel);
                    if (backend == null || backend.Provider == null)
                    {
                        Response.StatusCode = 503;
                        await Response.WriteAsync($"No available backend for model '{virtualModel.Name}'.");
                        return;
                    }
                    underlyingUrl = backend.Provider.BaseUrl.TrimEnd('/');
                    input.model = backend.UnderlyingModelName;
                    memoryUsageTracker.TrackUnderlyingModelUsage(backend.Provider.Id, backend.UnderlyingModelName);
                }
            }

            if (response == null) return;
            
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
        if (!await IsAuthorizedAsync())
        {
            return Unauthorized();
        }

        var virtualModels = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
            .ToListAsync();
        
        var allTags = new List<OllamaService.OllamaModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaModel>?>();

        foreach (var vm in virtualModels)
        {
            var backend = vm.VirtualModelBackends.FirstOrDefault();
            if (backend == null || backend.Provider == null) continue;
            var provider = backend.Provider;

            if (!providerCache.TryGetValue($"{provider.BaseUrl}_{provider.BearerToken}", out var physicalModels))
            {
                physicalModels = await ollamaService.GetDetailedModelsAsync(provider.BaseUrl, provider.BearerToken);
                providerCache[$"{provider.BaseUrl}_{provider.BearerToken}"] = physicalModels;
            }

            var physicalModel = physicalModels?.FirstOrDefault(m => m.Name == backend.UnderlyingModelName);
            if (physicalModel != null)
            {
                allTags.Add(new OllamaService.OllamaModel
                {
                    Name = vm.Name,
                    Model = vm.Name,
                    ModifiedAt = vm.CreatedAt,
                    Size = physicalModel.Size,
                    Digest = physicalModel.Digest,
                    Details = physicalModel.Details
                });
            }
            else
            {
                allTags.Add(new OllamaService.OllamaModel
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
                });
            }
        }

        return Ok(new { models = allTags });
    }

    [HttpGet("ps")]
    public async Task<IActionResult> Ps()
    {
        if (!await IsAuthorizedAsync())
        {
            return Unauthorized();
        }

        var virtualModels = await dbContext.VirtualModels
            .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
            .ToListAsync();
            
        var allRunning = new List<OllamaService.OllamaRunningModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaRunningModel>?>();

        foreach (var vm in virtualModels)
        {
            var backend = vm.VirtualModelBackends.FirstOrDefault();
            if (backend == null || backend.Provider == null) continue;
            var provider = backend.Provider;

            if (!providerCache.TryGetValue($"{provider.BaseUrl}_{provider.BearerToken}", out var runningPhysical))
            {
                runningPhysical = await ollamaService.GetRunningModelsAsync(provider.BaseUrl, provider.BearerToken);
                providerCache[$"{provider.BaseUrl}_{provider.BearerToken}"] = runningPhysical;
            }

            var physicalRunning = runningPhysical?.FirstOrDefault(m => m.Name == backend.UnderlyingModelName);
            if (physicalRunning != null)
            {
                allRunning.Add(new OllamaService.OllamaRunningModel
                {
                    Name = vm.Name,
                    Model = vm.Name,
                    ModifiedAt = vm.CreatedAt,
                    Size = physicalRunning.Size,
                    Digest = physicalRunning.Digest,
                    Details = physicalRunning.Details,
                    ExpiresAt = physicalRunning.ExpiresAt,
                    SizeVram = physicalRunning.SizeVram,
                    ContextLength = physicalRunning.ContextLength
                });
            }
        }

        return Ok(new { models = allRunning });
    }
}
