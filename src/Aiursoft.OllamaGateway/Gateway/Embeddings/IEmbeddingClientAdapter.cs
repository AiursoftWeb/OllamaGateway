using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public interface IEmbeddingClientAdapter
{
    ProtocolDialect Dialect { get; }

    DecodedEmbeddingRequest Decode(JsonObject body);

    Task WriteResponseAsync(
        GatewayEmbeddingProviderResponse providerResponse,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken);
}
