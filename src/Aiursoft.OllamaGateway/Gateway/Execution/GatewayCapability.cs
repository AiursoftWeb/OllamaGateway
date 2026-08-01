namespace Aiursoft.OllamaGateway.Gateway.Execution;

/// <summary>
/// Describes the semantic operation a backend must support. This is intentionally
/// independent from the client and provider wire protocols.
/// </summary>
public enum GatewayCapability
{
    ChatCompletion,
    TextGeneration,
    Embedding
}
