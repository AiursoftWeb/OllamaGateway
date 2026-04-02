using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.UsersViewModels;
[ExcludeFromCodeCoverage]
public class DeleteViewModel : UiStackLayoutViewModel
{
    public DeleteViewModel()
    {
        PageTitle = "Delete User";
    }

    [Display(Name = "User")]
    public required User User { get; set; }
}
