using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;

namespace Aiursoft.OllamaGateway.Models.UsersViewModels;
[ExcludeFromCodeCoverage]
public class UserRoleViewModel
{
    [Display(Name = "Role name")]
    public required string RoleName { get; set; }

    [Display(Name = "Is Selected")]
    public bool IsSelected { get; set; }
}
