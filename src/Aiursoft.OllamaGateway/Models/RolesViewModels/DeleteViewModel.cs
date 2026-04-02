using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.OllamaGateway.Models.RolesViewModels;
[ExcludeFromCodeCoverage]
public class DeleteViewModel : UiStackLayoutViewModel
{
    public DeleteViewModel()
    {
        PageTitle = "Delete Role";
    }

    [Display(Name = "Role")]
    public required IdentityRole Role { get; set; }
}
