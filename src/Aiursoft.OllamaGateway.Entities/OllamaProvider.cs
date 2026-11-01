using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Aiursoft.OllamaGateway.Entities;

public enum ProviderType
{
    Ollama = 0,
    OpenAI = 1
}

[ExcludeFromCodeCoverage]
public class OllamaProvider
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(100)]
    public required string BaseUrl { get; set; }

    [MaxLength(2000)]
    public string? BearerToken { get; set; }

    [MaxLength(100)]
    public string KeepAlive { get; set; } = "5m";

    [MaxLength(4000)]
    public string WarmupModelsJson { get; set; } = "[]";

    public bool KeepsModelLoaded(string modelName)
    {
        try
        {
            var models = JsonSerializer.Deserialize<List<WarmupModel>>(WarmupModelsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return models?.Any(model => model.Name == modelName) == true;
        }
        catch (JsonException)
        {
            // Invalid warmup configuration must not break ordinary inference.
            return false;
        }
    }

    public ProviderType ProviderType { get; set; } = ProviderType.Ollama;

    /// <summary>
    /// Whether this OpenAI-compatible provider accepts POST /v1/chat/completions.
    /// Null means that this provider predates provider-level protocol capabilities;
    /// its existing backend protocol remains authoritative until an administrator saves it.
    /// </summary>
    public bool? SupportsOpenAiChatCompletions { get; set; }

    /// <summary>
    /// Whether this OpenAI-compatible provider accepts POST /v1/responses.
    /// Null has the same legacy-inference meaning as SupportsOpenAiChatCompletions.
    /// </summary>
    public bool? SupportsOpenAiResponses { get; set; }

    public int MaxParallelism { get; set; }

    public int HealthCheckTimeoutSeconds { get; set; } = 60;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Obsolete("Use VirtualModelBackends instead")]
    public List<VirtualModel> VirtualModels { get; set; } = [];

    public List<VirtualModelBackend> VirtualModelBackends { get; set; } = [];
}
