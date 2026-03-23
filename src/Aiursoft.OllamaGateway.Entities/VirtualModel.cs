using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

public enum ModelType
{
    Chat,
    Embedding
}

[ExcludeFromCodeCoverage]
public class VirtualModel
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(100)]
    [Obsolete("Use VirtualModelBackends instead")]
    public string? UnderlyingModel { get; set; }

    [Obsolete("Use VirtualModelBackends instead")]
    public int? ProviderId { get; set; }

    [Obsolete("Use VirtualModelBackends instead")]
    public OllamaProvider? Provider { get; set; }

    public ModelType Type { get; set; }

    public SelectionStrategy SelectionStrategy { get; set; } = SelectionStrategy.PriorityFallback;

    public int MaxRetries { get; set; } = 3;

    public int HealthCheckTimeout { get; set; } = 30;

    public bool? Thinking { get; set; }

    public int? NumCtx { get; set; }

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }
    
    public int? NumPredict { get; set; }
    
    public float? RepeatPenalty { get; set; }

    public bool UseRawOutput { get; set; }

    [Obsolete("Use KeepAlive on Provider instead")]
    public bool KeepAlive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<VirtualModelBackend> VirtualModelBackends { get; set; } = [];
}
