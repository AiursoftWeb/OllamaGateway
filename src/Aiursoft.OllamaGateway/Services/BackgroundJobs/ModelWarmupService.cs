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
                await WarmupModelsAsync(stoppingToken);
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

            // Wait interval before next cycle
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }

        _logger.LogInformation("Model Warmup Service is stopping.");
    }

    private async Task WarmupModelsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

        var providers = await dbContext.OllamaProviders.ToListAsync(stoppingToken);

        if (!providers.Any())
        {
            return;
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        foreach (var provider in providers)
        {
            var warmupModels = System.Text.Json.JsonSerializer.Deserialize<List<WarmupModel>>(provider.WarmupModelsJson, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            
            if (warmupModels == null || !warmupModels.Any())
            {
                continue;
            }

            var underlyingUrl = provider.BaseUrl.TrimEnd('/');
            _logger.LogInformation("Warming up {Count} models on provider {Provider}...", warmupModels.Count, provider.Name);

            foreach (var warmupModel in warmupModels)
            {
                try
                {
                    // Ping as chat model first (most common)
                    var chatPayload = new
                    {
                        model = warmupModel.Name,
                        messages = new[] { new { role = "user", content = "keep alive" } },
                        stream = false,
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
                    _logger.LogInformation("Warmed up physical model {Underlying} on provider {Provider} (Status: {Status}, num_ctx: {NumCtx})", warmupModel.Name, provider.Name, response.StatusCode, warmupModel.NumCtx ?? 0);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to warmup physical model {Underlying} on provider {Provider}", warmupModel.Name, provider.Name);
                }
            }
        }
    }
}