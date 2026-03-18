using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.BackgroundJobs;

public class JobsIndexViewModel : UiStackLayoutViewModel
{
    public IEnumerable<JobInfo> AllRecentJobs { get; init; } = [];
}
