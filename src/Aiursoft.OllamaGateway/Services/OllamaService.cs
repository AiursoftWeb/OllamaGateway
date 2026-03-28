using System.Text.Json.Serialization;
using Aiursoft.Scanner.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace Aiursoft.OllamaGateway.Services;

public class OllamaService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    GlobalSettingsService globalSettingsService,
    ILogger<OllamaService> logger) : IScopedDependency
{
    public virtual async Task<List<OllamaModel>?> GetDetailedModelsAsync(string baseUrl, string? bearerToken = null)
    {
        var cacheKey = $"ollama_detailed_models_{baseUrl}_{bearerToken}";
        if (memoryCache.TryGetValue(cacheKey, out List<OllamaModel>? cachedModels))
        {
            return cachedModels;
        }

        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/tags";
            using var client = httpClientFactory.CreateClient();
            client.Timeout = await globalSettingsService.GetRequestTimeoutAsync();
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            }
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get detailed models from {BaseUrl}. Status: {StatusCode}", baseUrl, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = System.Text.Json.JsonSerializer.Deserialize<OllamaTagsResponse>(json);
            var models = result?.Models ?? new List<OllamaModel>();
            
            memoryCache.Set(cacheKey, models, TimeSpan.FromMinutes(1));
            return models;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception when getting detailed models from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public virtual async Task<List<OllamaRunningModel>?> GetRunningModelsAsync(string baseUrl, string? bearerToken = null, TimeSpan? overrideTimeout = null)
    {
        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/ps";
            using var client = httpClientFactory.CreateClient();
            client.Timeout = overrideTimeout ?? await globalSettingsService.GetRequestTimeoutAsync();
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            }
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get running models from {BaseUrl}. Status: {StatusCode}", baseUrl, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = System.Text.Json.JsonSerializer.Deserialize<OllamaPsResponse>(json);
            return result?.Models ?? new List<OllamaRunningModel>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception when getting running models from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public virtual async Task<List<string>?> GetUnderlyingModelsAsync(string baseUrl, string? bearerToken = null)
    {
        var models = await GetDetailedModelsAsync(baseUrl, bearerToken);
        return models?.Select(m => m.Name).ToList();
    }

    public virtual async Task<string?> GetVersionAsync(string baseUrl, string? bearerToken = null)
    {
        var cacheKey = $"ollama_version_{baseUrl}_{bearerToken}";
        if (memoryCache.TryGetValue(cacheKey, out string? cachedVersion))
        {
            return cachedVersion;
        }

        try
        {
            var url = baseUrl.TrimEnd('/') + "/api/version";
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5); // Version check should be fast
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            }
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to get version from {BaseUrl}. Status: {StatusCode}", baseUrl, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = System.Text.Json.JsonSerializer.Deserialize<OllamaVersionResponse>(json);
            var version = result?.Version;
            
            if (version != null)
            {
                memoryCache.Set(cacheKey, version, TimeSpan.FromMinutes(10));
            }
            return version;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception when getting version from {BaseUrl}", baseUrl);
            return null;
        }
    }

    public class OllamaTagsResponse { [JsonPropertyName("models")] public List<OllamaModel>? Models { get; set; } }
    public class OllamaPsResponse { [JsonPropertyName("models")] public List<OllamaRunningModel>? Models { get; set; } }
    public class OllamaVersionResponse { [JsonPropertyName("version")] public string? Version { get; set; } }

    public class OllamaModel
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("modified_at")] public DateTime ModifiedAt { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string Digest { get; set; } = string.Empty;
        [JsonPropertyName("details")] public OllamaModelDetails? Details { get; set; }
    }

    public class OllamaModelDetails
    {
        [JsonPropertyName("parent_model")] public string? ParentModel { get; set; }
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("family")] public string? Family { get; set; }
        [JsonPropertyName("families")] public List<string>? Families { get; set; }
        [JsonPropertyName("parameter_size")] public string? ParameterSize { get; set; }
        [JsonPropertyName("quantization_level")] public string? QuantizationLevel { get; set; }
    }

    public class OllamaRunningModel : OllamaModel
    {
        [JsonPropertyName("expires_at")] public DateTime ExpiresAt { get; set; }
        [JsonPropertyName("size_vram")] public long SizeVram { get; set; }
        [JsonPropertyName("context_length")] public int? ContextLength { get; set; }
    }
}
