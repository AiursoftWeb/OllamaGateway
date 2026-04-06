using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.DashboardViewModels;

public class MonitorViewModel : UiStackLayoutViewModel
{
    public MonitorViewModel()
    {
        PageTitle = "Monitor";
    }

    public List<VirtualModel> VirtualModels { get; set; } = [];
    public List<OllamaProvider> Providers { get; set; } = [];

    /// <summary>
    /// Set of virtual model names that currently have at least one active inference request.
    /// </summary>
    public HashSet<string> BusyModels { get; set; } = [];

    public Dictionary<int, DateTime?> BackendBanStatuses { get; set; } = [];
}

