namespace Aiursoft.OllamaGateway.Views.Shared.Components.RecentRequests;

public class RecentRequestsViewModel
{
    public List<RecentRequestItem> Items { get; set; } = [];
}

public class RecentRequestItem
{
    public string Status { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string BackendModelName { get; init; } = string.Empty;
    public string ApiKeyName { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string FullQuestion { get; init; } = string.Empty;
    public DateTime CompletedAt { get; init; }
    public double DurationMs { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}
