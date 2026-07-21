using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.DashboardViewModels;

[ExcludeFromCodeCoverage]
public class GuideViewModel : UiStackLayoutViewModel
{
    public GuideViewModel()
    {
        PageTitle = "Architecture Guide";
    }

    public string DefaultChatModelName { get; init; } = string.Empty;
    public VirtualModel? DefaultVirtualModel { get; init; }
    public List<OllamaProvider> Providers { get; init; } = [];
    public int TotalApiKeys { get; init; }
    public int TotalProviders { get; init; }
}
