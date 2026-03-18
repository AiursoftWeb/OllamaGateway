using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services.Authentication;
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

            // Wait 5 minutes before next cycle
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }

        _logger.LogInformation("Model Warmup Service is stopping.");
    }

    private async Task WarmupModelsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

        var modelsToWarmup = await dbContext.VirtualModels
            .Include(m => m.Provider)
            .Where(m => m.KeepAlive && m.Provider != null)
            .ToListAsync(stoppingToken);

        if (!modelsToWarmup.Any())
        {
            return;
        }

        _logger.LogInformation("Warming up {Count} models...", modelsToWarmup.Count);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        foreach (var model in modelsToWarmup)
        {
            if (model.Provider == null) continue;

            var underlyingUrl = model.Provider.BaseUrl.TrimEnd('/');
            
            try
            {
                if (model.Type == ModelType.Chat)
                {
                    var payload = new
                    {
                        model = model.UnderlyingModel,
                        messages = new[] { new { role = "user", content = "keep alive" } },
                        stream = false,
                        options = new
                        {
                            num_predict = 1,
                            num_ctx = model.NumCtx
                        }
                    };
                    
                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/chat")
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };

                    using var response = await client.SendAsync(request, stoppingToken);
                    _logger.LogInformation("Warmed up chat model {Model} on provider {Provider} (Status: {Status})", model.Name, model.Provider.Name, response.StatusCode);
                }
                else if (model.Type == ModelType.Embedding)
                {
                    var payload = new
                    {
                        model = model.UnderlyingModel,
                        prompt = "keep alive",
                        options = new
                        {
                            num_ctx = model.NumCtx
                        }
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embed")
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };

                    using var response = await client.SendAsync(request, stoppingToken);
                    _logger.LogInformation("Warmed up embedding model {Model} on provider {Provider} (Status: {Status})", model.Name, model.Provider.Name, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warmup model {Model} on provider {Provider}", model.Name, model.Provider.Name);
            }
        }
    }
}