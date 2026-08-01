using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class ChatRequestCompiler(
    IEnumerable<IChatRequestDecoder> requestDecoders,
    IEnumerable<IChatProviderRequestAdapter> providerAdapters) : IChatRequestCompiler
{
    public DecodedChatRequest Decode(ProtocolDialect clientDialect, string body)
    {
        return requestDecoders.Single(decoder => decoder.Dialect == clientDialect).Decode(body);
    }

    public HttpRequestMessage CreateProviderRequest(
        DecodedChatRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        var providerType = backend.Provider?.ProviderType
                           ?? throw new InvalidOperationException("Cannot compile a chat request without a provider.");
        return providerAdapters.Single(adapter => adapter.ProviderType == providerType)
            .CreateRequest(request, virtualModel, backend);
    }
}
