using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatProviderRequestAdapter
{
    ProviderType ProviderType { get; }

    ProtocolDialect Dialect { get; }

    HttpRequestMessage CreateRequest(
        DecodedChatRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend);
}
