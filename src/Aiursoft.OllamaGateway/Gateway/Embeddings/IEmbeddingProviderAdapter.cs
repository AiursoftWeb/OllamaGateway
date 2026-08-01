using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public interface IEmbeddingProviderAdapter
{
    ProviderType ProviderType { get; }

    ProtocolDialect Dialect { get; }

    HttpRequestMessage CreateRequest(
        DecodedEmbeddingRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend);

    GatewayEmbeddingProviderResponse DecodeResponse(string responseBody);
}
