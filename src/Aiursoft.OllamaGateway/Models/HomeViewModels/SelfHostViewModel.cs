using System.Diagnostics.CodeAnalysis;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.HomeViewModels;

[ExcludeFromCodeCoverage]
public class SelfHostViewModel : UiStackLayoutViewModel
{
    [Obsolete("This constructor is only used for framework!", true)]
    public SelfHostViewModel()
    {
    }

    public SelfHostViewModel(string title)
    {
        PageTitle = title;
    }
}
