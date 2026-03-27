using Aiursoft.OllamaGateway.Configuration;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class ModelWarmupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ModelWarmupService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public ModelWarmupService(
        IServiceProvider serviceProvider, 
        ILogger<ModelWarmupService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Model Warmup Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WarmupAllProvidersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while warming up models.");
            }

            int intervalMinutes = 5;
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var globalSettings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
                var intervalStr = await globalSettings.GetSettingValueAsync(SettingsMap.KeepAliveJobIntervalMinutes);
                if (int.TryParse(intervalStr, out var parsedInterval) && parsedInterval > 0)
                {
                    intervalMinutes = parsedInterval;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get KeepAliveJobIntervalMinutes setting. Using default 5 minutes.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
        }

        _logger.LogInformation("Model Warmup Service is stopping.");
    }

    private async Task WarmupAllProvidersAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

        var providers = await dbContext.OllamaProviders.AsNoTracking().ToListAsync(stoppingToken);
        if (!providers.Any())
        {
            return;
        }

        // Parallelize across different providers to avoid one slow server blocking others
        var tasks = providers.Select(p => WarmupSingleProviderAsync(p, stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task WarmupSingleProviderAsync(OllamaProvider provider, CancellationToken stoppingToken)
    {
        List<WarmupModel>? warmupModels;
        try
        {
            warmupModels = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize warmup models for provider {Provider}", provider.Name);
            return;
        }

        if (warmupModels == null || !warmupModels.Any())
        {
            return;
        }

        var underlyingUrl = provider.BaseUrl.TrimEnd('/');
        _logger.LogInformation("Warming up {Count} models on provider {Provider}...", warmupModels.Count, provider.Name);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        foreach (var warmupModel in warmupModels)
        {
            if (stoppingToken.IsCancellationRequested) break;
            
            try
            {
                // Ping as chat model with custom options
                var chatPayload = new
                {
                    model = warmupModel.Name,
                    messages = new[] { new { role = "user", content = "keep alive" } },
                    stream = false,
                    keep_alive = provider.KeepAlive,
                    options = new
                    {
                        num_predict = 1,
                        num_ctx = warmupModel.NumCtx,
                        temperature = warmupModel.Temperature,
                        top_p = warmupModel.TopP,
                        top_k = warmupModel.TopK
                    }
                };
                
                var json = System.Text.Json.JsonSerializer.Serialize(chatPayload, new System.Text.Json.JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });
                
                var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(provider.BearerToken))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.BearerToken);
                }

                using var response = await client.SendAsync(request, stoppingToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully warmed up physical model {Underlying} on provider {Provider} (Status: {Status}, num_ctx: {NumCtx})", 
                        warmupModel.Name, provider.Name, response.StatusCode, warmupModel.NumCtx ?? 0);
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    // The model may be an embedding-only model (e.g. bge-m3) that doesn't support /api/chat.
                    // Fallback to /api/embeddings with a minimal prompt to keep it loaded in memory.
                    _logger.LogInformation(
                        "Physical model {Underlying} on provider {Provider} returned BadRequest for /api/chat, trying /api/embeddings fallback...",
                        warmupModel.Name, provider.Name);

                    var embeddingPayload = new
                    {
                        model = warmupModel.Name,
                        prompt = "a",
                        keep_alive = provider.KeepAlive
                    };

                    var embeddingJson = System.Text.Json.JsonSerializer.Serialize(embeddingPayload);
                    var embeddingRequest = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embeddings")
                    {
                        Content = new StringContent(embeddingJson, System.Text.Encoding.UTF8, "application/json")
                    };

                    if (!string.IsNullOrWhiteSpace(provider.BearerToken))
                    {
                        embeddingRequest.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.BearerToken);
                    }

                    using var embeddingResponse = await client.SendAsync(embeddingRequest, stoppingToken);
                    if (embeddingResponse.IsSuccessStatusCode)
                    {
                        _logger.LogInformation(
                            "Successfully warmed up embedding model {Underlying} on provider {Provider} via /api/embeddings fallback",
                            warmupModel.Name, provider.Name);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Upstream returned {Status} while warming up physical model {Underlying} on provider {Provider} via /api/embeddings fallback",
                            embeddingResponse.StatusCode, warmupModel.Name, provider.Name);
                    }
                }
                else
                {
                    _logger.LogWarning("Upstream returned {Status} while warming up physical model {Underlying} on provider {Provider}", 
                        response.StatusCode, warmupModel.Name, provider.Name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to warmup physical model {Underlying} on provider {Provider}", warmupModel.Name, provider.Name);
            }
        }
    }
}
