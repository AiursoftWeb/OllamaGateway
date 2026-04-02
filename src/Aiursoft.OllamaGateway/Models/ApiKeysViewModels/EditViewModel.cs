using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ApiKeysViewModels;
[ExcludeFromCodeCoverage]
public class EditViewModel : UiStackLayoutViewModel
{
    public EditViewModel()
    {
        PageTitle = "Edit API Key";
    }

    [Required]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
}
