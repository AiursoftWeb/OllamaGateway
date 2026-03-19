using System.Diagnostics;
using System.Text;
using Aiursoft.GptClient.Abstractions;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Authentication;
using Aiursoft.OllamaGateway.Services.Clickhouse;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Aiursoft.OllamaGateway.Controllers;

[Route("api")]
[AllowAnonymous]
public class ProxyController(
    TemplateDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    ClickhouseDbContext clickhouseDbContext,
    OllamaService ollamaService,
    GlobalSettingsService globalSettingsService,
    ILogger<ProxyController> logger,
    MemoryUsageTracker memoryUsageTracker) : ControllerBase
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

        var sw = Stopwatch.StartNew();
        var log = new RequestLog
        {
            IP = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown",
            Method = Request.Method,
            Path = Request.Path,
            UserAgent = Request.Headers.UserAgent.ToString(),
            TraceId = HttpContext.TraceIdentifier,
            RequestTime = DateTime.UtcNow,
            UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous",
            ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous")
        };

        try
        {
            var modelToUse = string.IsNullOrWhiteSpace(input.Model) 
                ? await globalSettingsService.GetDefaultChatModelAsync() 
                : input.Model;

            var virtualModel = await dbContext.VirtualModels
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
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(virtualModel.Provider.Id, virtualModel.UnderlyingModel);
            
            log.Model = virtualModel.Name;
            log.ConversationMessageCount = input.Messages.Count;
            log.LastQuestion = input.Messages.LastOrDefault()?.Content ?? string.Empty;

            input.Model = virtualModel.UnderlyingModel;
            if (virtualModel.Thinking.HasValue) input.Think = virtualModel.Thinking.Value;
            
            input.Options ??= new OllamaRequestOptions();
            if (virtualModel.NumCtx.HasValue) input.Options.NumCtx = virtualModel.NumCtx;
            if (virtualModel.Temperature.HasValue) input.Options.Temperature = virtualModel.Temperature;
            if (virtualModel.TopP.HasValue) input.Options.TopP = virtualModel.TopP;
            if (virtualModel.TopK.HasValue) input.Options.TopK = virtualModel.TopK;
            if (virtualModel.NumPredict.HasValue) input.Options.NumPredict = virtualModel.NumPredict;
            if (virtualModel.RepeatPenalty.HasValue) input.Options.RepeatPenalty = virtualModel.RepeatPenalty;

            using var client = httpClientFactory.CreateClient();
            client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
            
            var json = JsonConvert.SerializeObject(input);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            logger.LogInformation("[{TraceId}] Proxying chat request for model {Model} to {UnderlyingUrl}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response);

            log.StatusCode = Response.StatusCode;
            log.Success = response.IsSuccessStatusCode;
            logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for chat request for model {Model}", HttpContext.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

            await using var responseStream = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted);
            
            if (input.Stream == true && response.IsSuccessStatusCode)
            {
                log.Answer = "[Streaming Response]";
                await responseStream.CopyToAsync(Response.Body, HttpContext.RequestAborted);
            }
            else
            {
                using var ms = new MemoryStream();
                await responseStream.CopyToAsync(ms);
                ms.Seek(0, SeekOrigin.Begin);
                var content = await new StreamReader(ms).ReadToEndAsync();
                
                try 
                {
                    var result = JsonConvert.DeserializeObject<dynamic>(content);
                    log.Answer = result?.message?.content ?? string.Empty;
                }
                catch { /* ignored */ }

                ms.Seek(0, SeekOrigin.Begin);
                await ms.CopyToAsync(Response.Body);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Chat request to Ollama was canceled by the client or timed out.");
            log.Success = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ProxyController.Chat");
            log.Success = false;
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            sw.Stop();
            log.Duration = sw.Elapsed.TotalMilliseconds;
            if (clickhouseDbContext.Enabled && clickhouseDbContext.RequestLogs != null)
            {
                clickhouseDbContext.RequestLogs.Add(log);
                await clickhouseDbContext.SaveChangesAsync();
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

        var sw = Stopwatch.StartNew();
        var log = new RequestLog
        {
            IP = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown",
            Method = Request.Method,
            Path = Request.Path,
            UserAgent = Request.Headers.UserAgent.ToString(),
            TraceId = HttpContext.TraceIdentifier,
            RequestTime = DateTime.UtcNow,
            UserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "Anonymous",
            ApiKeyName = User.FindFirst("ApiKeyName")?.Value ?? (User.Identity?.IsAuthenticated == true ? "Web Session" : "Anonymous")
        };

        try
        {
            string modelName = input.model;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = await globalSettingsService.GetDefaultEmbeddingModelAsync();
            }

            var virtualModel = await dbContext.VirtualModels
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
                memoryUsageTracker.TrackApiKeyUsage(apiKeyId);
            }
            memoryUsageTracker.TrackUnderlyingModelUsage(virtualModel.Provider.Id, virtualModel.UnderlyingModel);
            
            log.Model = virtualModel.Name;
            log.ConversationMessageCount = 1;
            log.LastQuestion = input.input?.ToString() ?? input.prompt?.ToString() ?? string.Empty;

            input.model = virtualModel.UnderlyingModel;

            using var client = httpClientFactory.CreateClient();
            client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
            
            var json = JsonConvert.SerializeObject(input);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embed")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            logger.LogInformation("[{TraceId}] Proxying embedding request for model {Model} to {UnderlyingUrl}", HttpContext.TraceIdentifier, virtualModel.Name, underlyingUrl);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted);
            
            Response.StatusCode = (int)response.StatusCode;
            CopyHeaders(response);

            log.StatusCode = Response.StatusCode;
            log.Success = response.IsSuccessStatusCode;
            logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for embedding request for model {Model}", HttpContext.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

            await response.Content.CopyToAsync(Response.Body, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Embedding request to Ollama was canceled by the client or timed out.");
            log.Success = false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in ProxyController.Embed");
            log.Success = false;
            if (!Response.HasStarted)
            {
                Response.StatusCode = 500;
                await Response.WriteAsync("Internal Server Error in Gateway.");
            }
        }
        finally
        {
            sw.Stop();
            log.Duration = sw.Elapsed.TotalMilliseconds;
            if (clickhouseDbContext.Enabled && clickhouseDbContext.RequestLogs != null)
            {
                clickhouseDbContext.RequestLogs.Add(log);
                await clickhouseDbContext.SaveChangesAsync();
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
            .Include(m => m.Provider)
            .ToListAsync();
        
        var allTags = new List<OllamaService.OllamaModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaModel>?>();

        foreach (var vm in virtualModels)
        {
            if (vm.Provider == null) continue;

            if (!providerCache.TryGetValue(vm.Provider.BaseUrl, out var physicalModels))
            {
                physicalModels = await ollamaService.GetDetailedModelsAsync(vm.Provider.BaseUrl);
                providerCache[vm.Provider.BaseUrl] = physicalModels;
            }

            var physicalModel = physicalModels?.FirstOrDefault(m => m.Name == vm.UnderlyingModel);
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
            .Include(m => m.Provider)
            .ToListAsync();
            
        var allRunning = new List<OllamaService.OllamaRunningModel>();
        var providerCache = new Dictionary<string, List<OllamaService.OllamaRunningModel>?>();

        foreach (var vm in virtualModels)
        {
            if (vm.Provider == null) continue;

            if (!providerCache.TryGetValue(vm.Provider.BaseUrl, out var runningPhysical))
            {
                runningPhysical = await ollamaService.GetRunningModelsAsync(vm.Provider.BaseUrl);
                providerCache[vm.Provider.BaseUrl] = runningPhysical;
            }

            var physicalRunning = runningPhysical?.FirstOrDefault(m => m.Name == vm.UnderlyingModel);
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
