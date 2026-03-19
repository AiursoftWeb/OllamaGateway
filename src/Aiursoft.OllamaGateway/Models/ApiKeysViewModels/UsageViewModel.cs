using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ApiKeysViewModels;

public class UsageViewModel : UiStackLayoutViewModel
{
    public UsageViewModel()
    {
        PageTitle = "API Key Usage Guide";
    }

    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyName { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string DefaultChatModel { get; set; } = string.Empty;
    public string DefaultEmbeddingModel { get; set; } = string.Empty;
}
