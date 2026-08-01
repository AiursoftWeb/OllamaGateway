using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

/// <summary>
/// Writes a response when the client and provider use the same wire protocol.
/// Implementations preserve provider framing and payload fidelity where possible.
/// </summary>
public interface IChatWireResponseWriter
{
    ProtocolDialect Dialect { get; }
    ProviderType ProviderType { get; }

    Task WriteAsync(
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        bool streaming,
        HttpContext httpContext);
}
