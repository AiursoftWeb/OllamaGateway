using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Services.BackgroundJobs;

public class ModelWarmupService(
    TemplateDbContext dbContext,
    IHttpClientFactory httpClientFactory,
    ILogger<ModelWarmupService> logger) : IBackgroundJob
{
    public string Name => "Model Warmup";
    public string Description => "Sends keep-alive requests to all configured Ollama providers to prevent model unloading from GPU/RAM.";

    public async Task ExecuteAsync()
    {
        var providers = await dbContext.OllamaProviders.AsNoTracking().ToListAsync();
        if (!providers.Any())
        {
            return;
        }

        logger.LogInformation("Starting global warmup routine for {Count} providers...", providers.Count);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Parallelize across different providers to avoid one slow server blocking others
        var tasks = providers.Select(p => WarmupSingleProviderAsync(p));
        await Task.WhenAll(tasks);

        sw.Stop();
        logger.LogInformation("Completed global warmup routine for {Count} providers in {Elapsed}ms.", providers.Count, sw.ElapsedMilliseconds);
    }

    private async Task WarmupSingleProviderAsync(OllamaProvider provider)
    {
        if (provider.ProviderType == ProviderType.OpenAI)
        {
            // OpenAI-compatible providers manage model loading automatically; warmup is not applicable
            logger.LogDebug("Skipping warmup for OpenAI-compatible provider {Provider}.", provider.Name);
            return;
        }

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
            logger.LogError(ex, "Failed to deserialize warmup models for provider {Provider}", provider.Name);
            return;
        }

        if (warmupModels == null || !warmupModels.Any())
        {
            return;
        }

        var underlyingUrl = provider.BaseUrl.TrimEnd('/');
        logger.LogInformation("Starting warmup for {Count} models on provider {Provider}...", warmupModels.Count, provider.Name);

        using var client = httpClientFactory.CreateClient();
        // Warmup may need to load large models into GPU/RAM, which can take minutes
        client.Timeout = TimeSpan.FromMinutes(10);

        int successCount = 0;
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        foreach (var warmupModel in warmupModels)
        {
            try
            {
                if (warmupModel.IsEmbedding)
                {
                    // Embedding models use /api/embeddings
                    var embeddingPayload = new
                    {
                        model = warmupModel.Name,
                        prompt = "a",
                        keep_alive = provider.KeepAlive
                    };

                    var json = System.Text.Json.JsonSerializer.Serialize(embeddingPayload);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{underlyingUrl}/api/embeddings")
                    {
                        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                    };

                    if (!string.IsNullOrWhiteSpace(provider.BearerToken))
                    {
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.BearerToken);
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await client.SendAsync(request);
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        successCount++;
                        logger.LogInformation(
                            "Successfully warmed up embedding model {Underlying} on provider {Provider} in {Elapsed}ms (keep_alive: {KeepAlive})",
                            warmupModel.Name, provider.Name, sw.ElapsedMilliseconds, provider.KeepAlive);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Upstream returned {Status} while warming up embedding model {Underlying} on provider {Provider} in {Elapsed}ms",
                            response.StatusCode, warmupModel.Name, provider.Name, sw.ElapsedMilliseconds);
                    }
                }
                else
                {
                    // Chat models use /api/chat
                    var chatPayload = new
                    {
                        model = warmupModel.Name,
                        messages = new[] { new { role = "user", content = "keep alive" } },
                        stream = false,
                        keep_alive = provider.KeepAlive,
                        options = new
                        {
                            num_predict = 1,
                            num_ctx = warmupModel.NumCtx
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

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var response = await client.SendAsync(request);
                    sw.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        successCount++;
                        logger.LogInformation("Successfully warmed up chat model {Underlying} on provider {Provider} in {Elapsed}ms (Status: {Status}, num_ctx: {NumCtx}, keep_alive: {KeepAlive})",
                            warmupModel.Name, provider.Name, sw.ElapsedMilliseconds, response.StatusCode, warmupModel.NumCtx ?? 0, provider.KeepAlive);
                    }
                    else
                    {
                        logger.LogWarning("Upstream returned {Status} while warming up chat model {Underlying} on provider {Provider} in {Elapsed}ms",
                            response.StatusCode, warmupModel.Name, provider.Name, sw.ElapsedMilliseconds);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to warmup physical model {Underlying} on provider {Provider}", warmupModel.Name, provider.Name);
            }
        }

        swTotal.Stop();
        logger.LogInformation("Finished warming up {SuccessCount}/{TotalCount} models on provider {Provider} in {Elapsed}ms.",
            successCount, warmupModels.Count, provider.Name, swTotal.ElapsedMilliseconds);
    }
}
