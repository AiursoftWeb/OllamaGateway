using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatCrossDialectResponseWriter
{
    Task WriteAsync(
        ProtocolDialect clientDialect,
        BackendProtocol providerProtocol,
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        bool streaming,
        HttpContext httpContext);
}
