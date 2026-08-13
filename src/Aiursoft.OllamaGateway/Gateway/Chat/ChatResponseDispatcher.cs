using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class ChatResponseDispatcher(
    IEnumerable<IChatWireResponseWriter> wireWriters,
    IChatCrossDialectResponseWriter crossDialectWriter) : IChatResponseDispatcher
{
    public Task WriteAsync(
        ProtocolDialect clientDialect,
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        VirtualModelBackend actualBackend,
        bool streaming,
        HttpContext httpContext)
    {
        var protocol = BackendProtocolResolver.Resolve(actualBackend);
        var wireWriter = wireWriters.SingleOrDefault(writer =>
            writer.Dialect == clientDialect && writer.Protocol == protocol);

        return wireWriter != null
            ? wireWriter.WriteAsync(upstreamResponse, virtualModel, streaming, httpContext)
            : crossDialectWriter.WriteAsync(
                clientDialect,
                upstreamResponse,
                virtualModel,
                actualBackend,
                streaming,
                httpContext);
    }
}
