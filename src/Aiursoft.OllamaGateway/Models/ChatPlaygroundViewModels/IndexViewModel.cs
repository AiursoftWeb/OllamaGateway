using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.OllamaGateway.Models.ChatPlaygroundViewModels;
[ExcludeFromCodeCoverage]
public class IndexViewModel : UiStackLayoutViewModel
{
    public IndexViewModel()
    {
        PageTitle = "Chat Playground";
    }

    public string Name { get; set; } = string.Empty;

    public string UnderlyingModel { get; set; } = string.Empty;

    public int ModelId { get; set; }

    public List<VirtualModel> AllModels { get; set; } = new();

    public string BaseUrl { get; set; } = string.Empty;

    public bool? Thinking { get; set; }

    public int? NumCtx { get; set; }

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }

    public bool IsOpenAIProvider { get; set; }
}
