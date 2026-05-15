using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

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

    public ProviderType ProviderType { get; set; } = ProviderType.Ollama;

    public int MaxParallelism { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Obsolete("Use VirtualModelBackends instead")]
    public List<VirtualModel> VirtualModels { get; set; } = [];

    public List<VirtualModelBackend> VirtualModelBackends { get; set; } = [];
}
