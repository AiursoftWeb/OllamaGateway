using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatClientResponseWriter
{
    ProtocolDialect Dialect { get; }

    Task WriteTranslatedAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        bool streaming,
        HttpResponse response,
        CancellationToken cancellationToken);
}
