using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.OllamaProvidersViewModels;
[ExcludeFromCodeCoverage]
public class CreateViewModel : UiStackLayoutViewModel
{
    public CreateViewModel()
    {
        PageTitle = "Create Ollama Provider";
    }

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string BaseUrl { get; set; } = "http://localhost:11434";

    [MaxLength(2000)]
    public string? BearerToken { get; set; }

    [Required]
    [MaxLength(100)]
    public string KeepAlive { get; set; } = "5m";

    public ProviderType ProviderType { get; set; } = ProviderType.Ollama;
}
