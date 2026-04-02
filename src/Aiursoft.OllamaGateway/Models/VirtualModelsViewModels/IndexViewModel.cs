using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;
[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Virtual Models";
    }

    public required List<VirtualModel> Models { get; set; }
    
    public Dictionary<int, string> ModelWarnings { get; set; } = new();

    public Dictionary<int, DateTime?> BackendBanStatuses { get; set; } = new();

    public ModelType CurrentType { get; set; }
}
