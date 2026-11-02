using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Views.Shared.Components.LiveInference;

public class LiveInferenceViewModel
{
    public List<LiveInferenceItem> Items { get; set; } = [];
    public int ActiveCount { get; set; }
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;

    public static LiveInferenceViewModel Create(ActiveRequestTracker tracker)
    {
        var items = tracker.GetSnapshot()
            .OrderByDescending(pair => pair.Value.ActiveCount > 0)
            .ThenByDescending(pair => pair.Value.LastStartedAt)
            .Select(pair => new LiveInferenceItem
            {
                ModelName = pair.Key,
                IsActive = pair.Value.ActiveCount > 0,
                ConcurrentCount = pair.Value.ActiveCount,
                LastQuestion = pair.Value.LastQuestion,
                FullQuestion = pair.Value.LastFullQuestion,
                BackendModelName = pair.Value.BackendModelName,
                ApiKeyName = pair.Value.ApiKeyName,
                LastStartedAt = pair.Value.LastStartedAt,
                LastCompletedAt = pair.Value.LastCompletedAt,
                Answer = pair.Value.LastAnswer,
                ErrorMessage = pair.Value.LastError
            }).ToList();
        return new LiveInferenceViewModel { Items = items, ActiveCount = items.Count(item => item.IsActive) };
    }
}

public class LiveInferenceItem
{
    public string ModelName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public int ConcurrentCount { get; init; }
    public string LastQuestion { get; init; } = string.Empty;
    public string FullQuestion { get; init; } = string.Empty;
    public string BackendModelName { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
    public string ApiKeyName { get; init; } = string.Empty;
    public DateTime LastStartedAt { get; init; }
    public DateTime? LastCompletedAt { get; init; }
}
