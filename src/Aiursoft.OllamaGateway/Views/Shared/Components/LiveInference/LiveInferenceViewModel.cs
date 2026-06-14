namespace Aiursoft.OllamaGateway.Views.Shared.Components.LiveInference;

public class LiveInferenceViewModel
{
    public List<LiveInferenceItem> Items { get; set; } = [];
    public int ActiveCount { get; set; }
}

public class LiveInferenceItem
{
    public string ModelName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public int ConcurrentCount { get; init; }
    public string LastQuestion { get; init; } = string.Empty;
    public string FullQuestion { get; init; } = string.Empty;
    public string BackendModelName { get; init; } = string.Empty;
    public string ApiKeyName { get; init; } = string.Empty;
    public DateTime LastStartedAt { get; init; }
    public DateTime? LastCompletedAt { get; init; }
}
