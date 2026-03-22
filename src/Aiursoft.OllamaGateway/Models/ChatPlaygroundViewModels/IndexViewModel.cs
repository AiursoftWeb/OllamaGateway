using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ChatPlaygroundViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Chat Playground";
    }

    public string Name { get; set; } = string.Empty;

    public string UnderlyingModel { get; set; } = string.Empty;

    public int ModelId { get; set; }

    public List<VirtualModel> AllModels { get; set; } = new();

    public string BaseUrl { get; set; } = string.Empty;
}
