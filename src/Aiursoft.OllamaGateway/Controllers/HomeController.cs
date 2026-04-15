using Aiursoft.OllamaGateway.Models.HomeViewModels;
using Aiursoft.OllamaGateway.Services;
using Aiursoft.UiStack.Navigation;
using Aiursoft.WebTools.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.OllamaGateway.Controllers;

[LimitPerMin]
public class HomeController : Controller
{
    public IActionResult Index()
    {
        return this.SimpleView(new IndexViewModel());
    }

    [RenderInNavBar(
        NavGroupName = "Self Host",
        NavGroupOrder = 10,
        CascadedLinksGroupName = "Deployment",
        CascadedLinksIcon = "server",
        CascadedLinksOrder = 1,
        LinkText = "Recommended local deployment",
        LinkOrder = 1)]
    public IActionResult SelfHost()
    {
        return this.StackView(new SelfHostViewModel("Self Host"));
    }
}
