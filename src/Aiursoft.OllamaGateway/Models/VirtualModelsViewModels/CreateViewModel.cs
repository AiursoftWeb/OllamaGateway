using System.ComponentModel.DataAnnotations;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.VirtualModelsViewModels;

public class CreateViewModel : UiStackLayoutViewModel
{
    public CreateViewModel()
    {
        PageTitle = "Virtual Model Settings";
    }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string UnderlyingModel { get; set; } = string.Empty;

    [Required]
    public int ProviderId { get; set; }

    public ModelType Type { get; set; }

    public bool? Thinking { get; set; }

    public int? NumCtx { get; set; }

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }
    
    public int? NumPredict { get; set; }
    
    public float? RepeatPenalty { get; set; }

    public bool UseRawOutput { get; set; }
    
    public bool KeepAlive { get; set; }
    
    public List<string> AvailableUnderlyingModels { get; set; } = new();

    public List<OllamaProvider> AvailableProviders { get; set; } = new();
}
