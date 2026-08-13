namespace Aiursoft.OllamaGateway.Gateway;

/// <summary>
/// Identifies an HTTP wire dialect. This is deliberately separate from the
/// provider vendor: one provider may expose more than one dialect.
/// </summary>
public enum ProtocolDialect
{
    OllamaNative,
    OpenAiChatCompletions,
    AnthropicMessages,
    OpenAiResponses
}
