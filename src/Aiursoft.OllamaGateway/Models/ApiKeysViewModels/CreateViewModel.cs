using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ApiKeysViewModels;
[ExcludeFromCodeCoverage]
public class CreateViewModel : UiStackLayoutViewModel
{
    public CreateViewModel()
    {
        PageTitle = "Create API Key";
    }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}
