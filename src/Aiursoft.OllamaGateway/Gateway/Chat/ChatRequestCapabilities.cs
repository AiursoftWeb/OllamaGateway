using Aiursoft.OllamaGateway.Gateway.Execution;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

internal static class ChatRequestCapabilities
{
    public static GatewayCapability Infer(
        bool streaming,
        IReadOnlyList<GatewayChatMessage> messages,
        IReadOnlyList<GatewayToolDefinition> tools,
        bool structuredOutput = false,
        bool nativeTools = false,
        bool statefulResponses = false,
        bool reasoningRequested = false)
    {
        var result = GatewayCapability.ChatCompletion;
        if (streaming) result |= GatewayCapability.Streaming;
        if (messages.SelectMany(message => message.Content).Any(part => part is GatewayImageContent))
            result |= GatewayCapability.ImageInput;
        if (tools.Count > 0 || messages.SelectMany(message => message.Content)
                .Any(part => part is GatewayToolCallContent or GatewayToolResultContent))
            result |= GatewayCapability.FunctionCalling;
        if (reasoningRequested || messages.SelectMany(message => message.Content).Any(part => part is GatewayReasoningContent))
            result |= GatewayCapability.Reasoning;
        if (structuredOutput) result |= GatewayCapability.StructuredOutput;
        if (nativeTools) result |= GatewayCapability.NativeTools;
        if (statefulResponses) result |= GatewayCapability.StatefulResponses;
        return result;
    }
}
