using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ErrorViewModels;
[ExcludeFromCodeCoverage]
public class ErrorViewModel: UiStackLayoutViewModel
{
    public ErrorViewModel()
    {
        PageTitle = "Error";
    }

    [Display(Name = "Error code")]
    public int ErrorCode { get; set; } = 500;

    [Display(Name = "Request ID")]
    public required string RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    [Display(Name = "Return URL")]
    public string? ReturnUrl { get; set; }
}
