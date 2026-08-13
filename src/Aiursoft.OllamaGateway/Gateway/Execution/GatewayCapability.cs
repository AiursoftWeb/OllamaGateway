namespace Aiursoft.OllamaGateway.Gateway.Execution;

/// <summary>
/// Describes the semantic operation a backend must support. This is intentionally
/// independent from the client and provider wire protocols.
/// </summary>
[Flags]
public enum GatewayCapability
{
    None = 0,
    ChatCompletion = 1 << 0,
    TextGeneration = 1 << 1,
    Embedding = 1 << 2,
    Streaming = 1 << 3,
    ImageInput = 1 << 4,
    FunctionCalling = 1 << 5,
    Reasoning = 1 << 6,
    StructuredOutput = 1 << 7,
    NativeTools = 1 << 8,
    StatefulResponses = 1 << 9,
    OllamaNativePassthrough = 1 << 10,
    OpenAiChatPassthrough = 1 << 11,
    OpenAiResponsesPassthrough = 1 << 12,
    AnthropicPassthrough = 1 << 13
}
