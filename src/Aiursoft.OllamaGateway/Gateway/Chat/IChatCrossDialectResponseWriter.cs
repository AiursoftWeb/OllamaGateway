using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatCrossDialectResponseWriter
{
    Task WriteAsync(
        ProtocolDialect clientDialect,
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        VirtualModelBackend actualBackend,
        bool streaming,
        HttpContext httpContext);
}
