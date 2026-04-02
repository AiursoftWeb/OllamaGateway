using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.OllamaProvidersViewModels;
[ExcludeFromCodeCoverage]
public class ProviderStatus
{
    public required OllamaProvider Provider { get; set; }
    public bool IsAlive { get; set; }
    public string? Version { get; set; }
    public List<OllamaService.OllamaRunningModel>? RunningModels { get; set; }
}
[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Ollama Providers";
    }

    public required List<ProviderStatus> ProviderStatuses { get; set; }
}
