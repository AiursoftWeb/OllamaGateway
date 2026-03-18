using System.ComponentModel.DataAnnotations;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.OllamaGateway.Models.PermissionsViewModels;

public class DetailsViewModel : UiStackLayoutViewModel
{
    public DetailsViewModel()
    {
        PageTitle = "Permission Details";
    }

    [Display(Name = "Permission")]
    public required PermissionDescriptor Permission { get; set; }

    [Display(Name = "Roles")]
    public required List<IdentityRole> Roles { get; set; }

    [Display(Name = "Users")]
    public required List<User> Users { get; set; }
}
