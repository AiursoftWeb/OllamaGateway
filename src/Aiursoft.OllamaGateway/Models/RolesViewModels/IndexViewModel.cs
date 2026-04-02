using System.Diagnostics.CodeAnalysis;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.RolesViewModels;
[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Roles";
    }

    public required List<IdentityRoleWithCount> Roles { get; init; }
}
