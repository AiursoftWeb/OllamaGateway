using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Views.Shared.Components.LiveInference;

public class LiveInference(ActiveRequestTracker activeRequestTracker) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var all = activeRequestTracker.GetAll();
        var items = all
            .OrderByDescending(kv => kv.Value.ActiveCount > 0)
            .ThenByDescending(kv => kv.Value.LastStartedAt)
            .Select(kv => new LiveInferenceItem
            {
                ModelName = kv.Key,
                IsActive = kv.Value.ActiveCount > 0,
                ConcurrentCount = kv.Value.ActiveCount,
                LastQuestion = kv.Value.LastQuestion,
                FullQuestion = kv.Value.LastFullQuestion,
                BackendModelName = kv.Value.BackendModelName,
                ApiKeyName = kv.Value.ApiKeyName,
                LastStartedAt = kv.Value.LastStartedAt,
                LastCompletedAt = kv.Value.LastCompletedAt
            })
            .ToList();

        var model = new LiveInferenceViewModel
        {
            Items = items,
            ActiveCount = items.Count(i => i.IsActive)
        };

        return View(model);
    }
}
