using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<VirtualModel> VirtualModels { get; set; } = [];
}
