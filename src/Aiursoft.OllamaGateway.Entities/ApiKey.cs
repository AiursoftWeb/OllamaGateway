using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.OllamaGateway.Entities;

[ExcludeFromCodeCoverage]
public class ApiKey
{
    [Key]
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(100)]
    public required string Key { get; set; }

    public required string UserId { get; set; }
    public User? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsed { get; set; }
    public long UsageCount { get; set; }

    public int MaxRequests { get; set; } = 10;
    public int TimeWindowSeconds { get; set; } = 15;
    public bool RateLimitEnabled { get; set; }
    public bool RateLimitHang { get; set; } = true;
}
