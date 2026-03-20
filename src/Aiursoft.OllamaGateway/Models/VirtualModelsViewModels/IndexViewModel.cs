using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Virtual Models";
    }

    public required List<VirtualModel> Models { get; set; }
    
    public Dictionary<int, string> ModelWarnings { get; set; } = new();

    public ModelType CurrentType { get; set; }
}
