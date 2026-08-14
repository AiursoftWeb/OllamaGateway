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
        var protocol = BackendProtocolResolver.Resolve(backend, request.SourceDialect);
        return providerAdapters.Single(adapter => adapter.Protocol == protocol)
            .CreateRequest(request, virtualModel, backend);
    }
}
