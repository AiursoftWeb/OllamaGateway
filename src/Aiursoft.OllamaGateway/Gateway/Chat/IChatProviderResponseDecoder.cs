using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public interface IChatProviderResponseDecoder
{
    BackendProtocol Protocol { get; }

    IAsyncEnumerable<GatewayChatEvent> DecodeAsync(
        Stream responseStream,
        bool streaming,
        CancellationToken cancellationToken);
}
