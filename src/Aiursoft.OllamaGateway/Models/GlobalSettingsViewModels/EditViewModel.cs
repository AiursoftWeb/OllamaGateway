using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;

namespace Aiursoft.OllamaGateway.Models.GlobalSettingsViewModels;
[ExcludeFromCodeCoverage]
public class EditViewModel
{
    [Required(ErrorMessage = "The {0} is required.")]
    [Display(Name = "Key")]
    public string Key { get; set; } = string.Empty;

    [Display(Name = "Value")]
    public string? Value { get; set; }
}
