using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

// ReSharper disable once NotAccessedPositionalProperty.Global
public sealed record GatewayEmbeddingRequest(string InputJson);

public sealed record DecodedEmbeddingRequest(
    ProtocolDialect SourceDialect,
    GatewayEmbeddingRequest Request,
    JsonObject OriginalBody);

public sealed record GatewayEmbeddingProviderResponse(
    ProtocolDialect ProviderDialect,
    JsonObject OriginalBody,
    JsonArray Embeddings,
    long PromptTokens);
