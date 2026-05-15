using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Views.Shared.Components.RecentRequests;

public class RecentRequests(ActiveRequestTracker activeRequestTracker) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var entries = activeRequestTracker.GetRecentRequests();
        var items = entries.Select(r => new RecentRequestItem
        {
            Status = r.Status,
            ModelName = r.ModelName,
            BackendModelName = r.BackendModelName,
            ApiKeyName = r.ApiKeyName,
            Question = r.Question,
            CompletedAt = r.CompletedAt,
            DurationMs = r.DurationMs
        }).ToList();

        return View(new RecentRequestsViewModel { Items = items });
    }
}
