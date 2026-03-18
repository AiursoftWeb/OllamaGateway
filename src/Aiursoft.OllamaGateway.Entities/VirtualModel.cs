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
    public required string UnderlyingModel { get; set; }

    public int ProviderId { get; set; }
    public OllamaProvider? Provider { get; set; }

    public ModelType Type { get; set; }

    public bool? Thinking { get; set; }

    public int? NumCtx { get; set; }

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }
    
    public int? NumPredict { get; set; }
    
    public float? RepeatPenalty { get; set; }

    public bool UseRawOutput { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
