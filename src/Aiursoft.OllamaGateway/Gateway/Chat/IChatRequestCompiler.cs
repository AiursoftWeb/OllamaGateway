using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatRequestCompiler
{
    DecodedChatRequest Decode(ProtocolDialect clientDialect, string body);

    HttpRequestMessage CreateProviderRequest(
        DecodedChatRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend);
}
