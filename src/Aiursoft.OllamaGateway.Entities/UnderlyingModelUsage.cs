using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

[ExcludeFromCodeCoverage]
public class UnderlyingModelUsage
{
    [Key]
    public int Id { get; set; }

    public int ProviderId { get; set; }
    public OllamaProvider? Provider { get; set; }

    [MaxLength(100)]
    public required string ModelName { get; set; }

    public long UsageCount { get; set; }

    public DateTime? LastUsed { get; set; }
}
