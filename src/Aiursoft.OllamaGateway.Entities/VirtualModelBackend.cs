using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

/// <summary>
/// The HTTP protocol used to invoke one physical model. This belongs to the
/// backend rather than the provider because one provider can expose multiple
/// protocols and individual models may support different endpoint families.
/// Null is retained for existing rows and means "infer from ProviderType".
/// </summary>
public enum BackendProtocol
{
    OllamaNative = 0,
    OpenAiChatCompletions = 1,
    OpenAiResponses = 2
}

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

    public BackendProtocol? Protocol { get; set; }

    public int Priority { get; set; }

    public int Weight { get; set; }

    public bool Enabled { get; set; }

    public bool IsHealthy { get; set; }

    public bool IsReady { get; set; }

    [MaxLength(100)]
    public string? KeepAlive { get; set; }

    public DateTime LastCheckedAt { get; set; }
}
