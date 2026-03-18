using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ApiKeysViewModels;

public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "API Keys";
    }

    public required List<ApiKey> ApiKeys { get; set; }
    public Dictionary<int, DateTime?> LastUsedTimes { get; set; } = new();
    public Dictionary<int, long> TotalCalls { get; set; } = new();
}
