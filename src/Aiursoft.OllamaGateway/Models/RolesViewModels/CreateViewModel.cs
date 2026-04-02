using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.RolesViewModels;
[ExcludeFromCodeCoverage]
public class CreateViewModel: UiStackLayoutViewModel
{
    public CreateViewModel()
    {
        PageTitle = "Create Role";
    }

    [Required(ErrorMessage = "The {0} is required.")]
    [Display(Name = "Role Name")]
    public string? RoleName { get; set; }
}
