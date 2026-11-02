using Aiursoft.OllamaGateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Views.Shared.Components.LiveInference;

public class LiveInference(ActiveRequestTracker activeRequestTracker) : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var model = LiveInferenceViewModel.Create(activeRequestTracker);

        return View(model);
    }
}
