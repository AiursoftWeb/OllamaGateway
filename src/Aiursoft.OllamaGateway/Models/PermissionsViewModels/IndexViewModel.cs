using System.Diagnostics.CodeAnalysis;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.PermissionsViewModels;
[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Permissions";
    }

    public required List<PermissionWithRoleCount> Permissions { get; init; }
}
