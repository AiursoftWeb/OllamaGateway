using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatProviderResponseDecoder
{
    ProviderType ProviderType { get; }

    ProtocolDialect Dialect { get; }

    IAsyncEnumerable<GatewayChatEvent> DecodeAsync(
        Stream responseStream,
        bool streaming,
        CancellationToken cancellationToken);
}
