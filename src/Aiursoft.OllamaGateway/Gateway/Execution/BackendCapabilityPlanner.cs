using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Execution;

/// <summary>
/// Central capability matrix for provider protocols. Keep transport-specific
/// limitations here instead of distributing provider checks across controllers.
/// </summary>
public sealed class BackendCapabilityPlanner : IBackendCapabilityPlanner
{
    public bool Supports(VirtualModelBackend backend, GatewayCapability capability)
    {
        if (backend.Provider == null)
            return false;

        var supported = BackendProtocolResolver.Resolve(backend) switch
        {
            BackendProtocol.OllamaNative =>
                GatewayCapability.ChatCompletion |
                GatewayCapability.TextGeneration |
                GatewayCapability.Embedding |
                GatewayCapability.Streaming |
                GatewayCapability.ImageInput |
                GatewayCapability.FunctionCalling |
                GatewayCapability.Reasoning |
                GatewayCapability.StructuredOutput |
                GatewayCapability.OllamaNativePassthrough,
            BackendProtocol.OpenAiChatCompletions =>
                GatewayCapability.ChatCompletion |
                GatewayCapability.Embedding |
                GatewayCapability.Streaming |
                GatewayCapability.ImageInput |
                GatewayCapability.FunctionCalling |
                GatewayCapability.Reasoning |
                GatewayCapability.StructuredOutput |
                GatewayCapability.OpenAiChatPassthrough,
            BackendProtocol.OpenAiResponses =>
                GatewayCapability.ChatCompletion |
                GatewayCapability.Embedding |
                GatewayCapability.Streaming |
                GatewayCapability.ImageInput |
                GatewayCapability.FunctionCalling |
                GatewayCapability.Reasoning |
                GatewayCapability.StructuredOutput |
                GatewayCapability.NativeTools |
                GatewayCapability.OpenAiResponsesPassthrough,
            _ => GatewayCapability.None
        };

        return (supported & capability) == capability;
    }
}
