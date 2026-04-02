using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;

namespace Aiursoft.OllamaGateway.Models.ManageViewModels;
[ExcludeFromCodeCoverage]
public class SwitchThemeViewModel
{
    [Required(ErrorMessage = "The {0} is required.")]
    [Display(Name = "Theme")]
    public required string Theme { get; set; }
}
