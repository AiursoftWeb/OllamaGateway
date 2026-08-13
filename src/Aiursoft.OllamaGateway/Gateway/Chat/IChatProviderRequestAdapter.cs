using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatProviderRequestAdapter
{
    BackendProtocol Protocol { get; }

    HttpRequestMessage CreateRequest(
        DecodedChatRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend);
}
