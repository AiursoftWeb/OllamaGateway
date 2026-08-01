using System.Text;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

internal sealed class ChatResponseState
{
    public string ResponseId { get; set; } = $"chatcmpl-{Guid.NewGuid():N}";
    public long CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public StringBuilder Text { get; } = new();
    public StringBuilder Reasoning { get; } = new();
    public SortedDictionary<int, ToolCallState> Tools { get; } = new();
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public GatewayFinishReason FinishReason { get; set; } = GatewayFinishReason.Stop;

    public ToolCallState Tool(int index)
    {
        if (!Tools.TryGetValue(index, out var tool))
        {
            tool = new ToolCallState();
            Tools[index] = tool;
        }
        return tool;
    }
}

internal sealed class ToolCallState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StringBuilder Arguments { get; } = new();
    public bool StartEmitted { get; set; }
}
