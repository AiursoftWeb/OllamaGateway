using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway;

public static class BackendProtocolResolver
{
    public static BackendProtocol Resolve(VirtualModelBackend backend)
    {
        if (backend.Protocol.HasValue)
            return backend.Protocol.Value;

        return backend.Provider?.ProviderType switch
        {
            ProviderType.Ollama => BackendProtocol.OllamaNative,
            ProviderType.OpenAI => BackendProtocol.OpenAiChatCompletions,
            _ => throw new InvalidOperationException("Cannot infer a backend protocol without a provider.")
        };
    }

    public static ProtocolDialect ToDialect(BackendProtocol protocol) => protocol switch
    {
        BackendProtocol.OllamaNative => ProtocolDialect.OllamaNative,
        BackendProtocol.OpenAiChatCompletions => ProtocolDialect.OpenAiChatCompletions,
        BackendProtocol.OpenAiResponses => ProtocolDialect.OpenAiResponses,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };
}
