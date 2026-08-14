using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway;

public static class BackendProtocolResolver
{
    public static BackendProtocol Resolve(
        VirtualModelBackend backend,
        ProtocolDialect? clientDialect = null)
    {
        var supported = GetSupportedProtocols(backend);

        var matchingProtocol = clientDialect switch
        {
            ProtocolDialect.OpenAiChatCompletions => BackendProtocol.OpenAiChatCompletions,
            ProtocolDialect.OpenAiResponses => BackendProtocol.OpenAiResponses,
            ProtocolDialect.OllamaNative => BackendProtocol.OllamaNative,
            _ => (BackendProtocol?)null
        };
        if (matchingProtocol.HasValue && supported.Contains(matchingProtocol.Value))
            return matchingProtocol.Value;

        if (backend.Protocol.HasValue && supported.Contains(backend.Protocol.Value))
            return backend.Protocol.Value;

        if (supported.Contains(BackendProtocol.OpenAiChatCompletions))
            return BackendProtocol.OpenAiChatCompletions;

        return supported[0];
    }

    public static IReadOnlyList<BackendProtocol> GetSupportedProtocols(VirtualModelBackend backend)
    {
        var provider = backend.Provider
                       ?? throw new InvalidOperationException("Cannot resolve a backend protocol without a provider.");

        // Preserve the existing ability to explicitly expose a non-default wire protocol
        // from an Ollama provider, or Ollama-native HTTP from an OpenAI provider.
        if (provider.ProviderType == ProviderType.Ollama)
            return [backend.Protocol ?? BackendProtocol.OllamaNative];
        if (backend.Protocol == BackendProtocol.OllamaNative)
            return [BackendProtocol.OllamaNative];

        if (HasExplicitOpenAiCapabilities(provider))
            return GetProviderSupportedProtocols(provider);

        // Before provider-level capabilities existed, Protocol belonged to each backend.
        // Keep persisted legacy backends on exactly that dialect. A transient physical-model
        // backend can instead use the aggregate inferred from the provider's loaded backends.
        if (backend.Id != 0 || backend.Protocol.HasValue)
            return [backend.Protocol ?? BackendProtocol.OpenAiChatCompletions];

        return GetProviderSupportedProtocols(provider);
    }

    public static IReadOnlyList<BackendProtocol> GetProviderSupportedProtocols(OllamaProvider provider)
    {
        if (provider.ProviderType == ProviderType.Ollama)
            return [BackendProtocol.OllamaNative];

        var protocols = new List<BackendProtocol>(2);
        if (HasExplicitOpenAiCapabilities(provider))
        {
            if (provider.SupportsOpenAiChatCompletions == true)
                protocols.Add(BackendProtocol.OpenAiChatCompletions);
            if (provider.SupportsOpenAiResponses == true)
                protocols.Add(BackendProtocol.OpenAiResponses);
        }
        else
        {
            protocols.AddRange(provider.VirtualModelBackends
                .Select(backend => backend.Protocol ?? BackendProtocol.OpenAiChatCompletions)
                .Where(protocol => protocol is BackendProtocol.OpenAiChatCompletions or BackendProtocol.OpenAiResponses)
                .Distinct());

            if (protocols.Count == 0)
                protocols.Add(BackendProtocol.OpenAiChatCompletions);
        }

        if (protocols.Count == 0)
        {
            throw new InvalidOperationException(
                $"OpenAI-compatible provider '{provider.Name}' has no enabled generation protocol.");
        }

        return protocols;
    }

    private static bool HasExplicitOpenAiCapabilities(OllamaProvider provider) =>
        provider.SupportsOpenAiChatCompletions.HasValue || provider.SupportsOpenAiResponses.HasValue;

    public static ProtocolDialect ToDialect(BackendProtocol protocol) => protocol switch
    {
        BackendProtocol.OllamaNative => ProtocolDialect.OllamaNative,
        BackendProtocol.OpenAiChatCompletions => ProtocolDialect.OpenAiChatCompletions,
        BackendProtocol.OpenAiResponses => ProtocolDialect.OpenAiResponses,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
    };
}
