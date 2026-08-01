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
        return backend.Provider?.ProviderType switch
        {
            ProviderType.Ollama => capability is
                GatewayCapability.ChatCompletion or
                GatewayCapability.TextGeneration or
                GatewayCapability.Embedding,
            ProviderType.OpenAI => capability is
                GatewayCapability.ChatCompletion or
                GatewayCapability.Embedding,
            _ => false
        };
    }
}
