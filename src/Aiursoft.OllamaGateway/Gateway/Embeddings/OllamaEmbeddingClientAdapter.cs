using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public sealed class OllamaEmbeddingClientAdapter : IEmbeddingClientAdapter
{
    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

    public DecodedEmbeddingRequest Decode(JsonObject body)
    {
        var input = body["input"] ?? body["prompt"];
        return new DecodedEmbeddingRequest(
            Dialect,
            new GatewayEmbeddingRequest(input?.ToJsonString() ?? "null"),
            body.DeepClone().AsObject());
    }

    public async Task WriteResponseAsync(
        GatewayEmbeddingProviderResponse providerResponse,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        JsonObject responseBody;
        if (providerResponse.ProviderDialect == Dialect)
        {
            responseBody = providerResponse.OriginalBody.DeepClone().AsObject();
            responseBody["model"] = virtualModel.Name;
        }
        else
        {
            responseBody = new JsonObject
            {
                ["model"] = virtualModel.Name,
                ["embeddings"] = providerResponse.Embeddings.DeepClone()
            };
        }

        response.ContentType = "application/json";
        await response.WriteAsync(responseBody.ToJsonString(), cancellationToken);
    }
}
