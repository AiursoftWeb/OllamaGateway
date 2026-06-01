using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.AllApiKeysViewModels;

[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "All API Keys";
    }

    public required List<ApiKey> ApiKeys { get; set; }
    public Dictionary<int, string> CreatorDisplayNames { get; set; } = new();
    public Dictionary<int, DateTime?> LastUsedTimes { get; set; } = new();
    public Dictionary<int, long> TotalCalls { get; set; } = new();
    public Dictionary<int, string> TopModels { get; set; } = new();
    public Dictionary<int, double> TopModelPercentages { get; set; } = new();
}
