using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

[ExcludeFromCodeCoverage]
public class VirtualModelBackend
{
    [Key]
    public int Id { get; set; }

    public int VirtualModelId { get; set; }
    public VirtualModel? VirtualModel { get; set; }

    public int ProviderId { get; set; }
    public OllamaProvider? Provider { get; set; }

    [MaxLength(100)]
    public required string UnderlyingModelName { get; set; }

    public int Priority { get; set; }

    public int Weight { get; set; }

    public bool Enabled { get; set; }

    public bool IsHealthy { get; set; }

    public bool IsReady { get; set; }

    [MaxLength(100)]
    public string? KeepAlive { get; set; }

    public DateTime LastCheckedAt { get; set; }
}
