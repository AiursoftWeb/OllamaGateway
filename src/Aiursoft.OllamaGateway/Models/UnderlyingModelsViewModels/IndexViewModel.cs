using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.UnderlyingModelsViewModels;

public class UnderlyingModelInfo
{
    public required OllamaProvider Provider { get; set; }
    public required OllamaService.OllamaModel RawModel { get; set; }
    public bool IsRunning { get; set; }
    public List<VirtualModel> UsedByVirtualModels { get; set; } = new();
}

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Underlying Models";
    }

    public List<UnderlyingModelInfo> Models { get; set; } = new();
}
