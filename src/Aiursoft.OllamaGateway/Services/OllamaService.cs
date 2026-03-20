using Aiursoft.Scanner.Abstractions;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace Aiursoft.OllamaGateway.Services;

public class OllamaService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    GlobalSettingsService globalSettingsService) : IScopedDependency
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
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<OllamaTagsResponse>(json);
            var models = result?.Models ?? new List<OllamaModel>();
            
            memoryCache.Set(cacheKey, models, TimeSpan.FromMinutes(1));
            return models;
        }
        catch (Exception)
        {
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
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<OllamaPsResponse>(json);
            return result?.Models ?? new List<OllamaRunningModel>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public virtual async Task<List<string>?> GetUnderlyingModelsAsync(string baseUrl, string? bearerToken = null)
    {
        var models = await GetDetailedModelsAsync(baseUrl, bearerToken);
        return models?.Select(m => m.Name).ToList();
    }

    public class OllamaTagsResponse { [JsonProperty("models")] public List<OllamaModel>? Models { get; set; } }
    public class OllamaPsResponse { [JsonProperty("models")] public List<OllamaRunningModel>? Models { get; set; } }

    public class OllamaModel
    {
        [JsonProperty("name")] public string Name { get; set; } = string.Empty;
        [JsonProperty("model")] public string Model { get; set; } = string.Empty;
        [JsonProperty("modified_at")] public DateTime ModifiedAt { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("digest")] public string Digest { get; set; } = string.Empty;
        [JsonProperty("details")] public OllamaModelDetails? Details { get; set; }
    }

    public class OllamaModelDetails
    {
        [JsonProperty("parent_model")] public string? ParentModel { get; set; }
        [JsonProperty("format")] public string? Format { get; set; }
        [JsonProperty("family")] public string? Family { get; set; }
        [JsonProperty("families")] public List<string>? Families { get; set; }
        [JsonProperty("parameter_size")] public string? ParameterSize { get; set; }
        [JsonProperty("quantization_level")] public string? QuantizationLevel { get; set; }
    }

    public class OllamaRunningModel : OllamaModel
    {
        [JsonProperty("expires_at")] public DateTime ExpiresAt { get; set; }
        [JsonProperty("size_vram")] public long SizeVram { get; set; }
        [JsonProperty("context_length")] public int? ContextLength { get; set; }
    }
}
