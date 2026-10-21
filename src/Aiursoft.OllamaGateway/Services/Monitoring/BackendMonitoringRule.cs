using System.Text.Json;

namespace Aiursoft.OllamaGateway.Services.Monitoring;

public sealed class BackendMonitoringRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "failures";
    public int Minutes { get; set; } = 10;
    public int FailureCount { get; set; } = 5;
    public string Severity { get; set; } = "sev3";
    public bool IsEnabled { get; set; } = true;

    public static string DefaultJson { get; } = JsonSerializer.Serialize(new[]
    {
        new BackendMonitoringRule { FailureCount = 5 },
        new BackendMonitoringRule { FailureCount = 20, Severity = "sev2" },
        new BackendMonitoringRule { Kind = "offline", Minutes = 10 },
        new BackendMonitoringRule { Kind = "offline", Minutes = 40, Severity = "sev2" }
    });

    public static List<BackendMonitoringRule> Parse(string json)
    {
        var rules = JsonSerializer.Deserialize<List<BackendMonitoringRule?>>(json)
                    ?? throw new InvalidOperationException("Monitoring rules must be a JSON array.");
        if (rules.Count > 20 || rules.Any(rule => rule == null || !rule.IsValid()) ||
            rules.Select(rule => rule!.Id).Distinct().Count() != rules.Count)
            throw new InvalidOperationException("Use at most 20 unique rules, windows from 1 to 60 minutes, nonnegative failure thresholds and sev0 to sev4.");
        return rules.Select(rule => rule!).ToList();
    }

    public bool IsValid() => Id != Guid.Empty && Kind is "failures" or "offline" && Minutes is >= 1 and <= 60 &&
        FailureCount is >= 0 and <= 1000000 && Severity is "sev0" or "sev1" or "sev2" or "sev3" or "sev4";
}
