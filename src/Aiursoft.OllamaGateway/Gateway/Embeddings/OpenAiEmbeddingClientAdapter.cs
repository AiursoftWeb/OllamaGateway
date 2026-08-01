using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public sealed class OpenAiEmbeddingClientAdapter : IEmbeddingClientAdapter
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;

    public DecodedEmbeddingRequest Decode(JsonObject body)
    {
        var input = body["input"] ?? throw new InvalidOperationException("OpenAI embedding request has no input.");
        return new DecodedEmbeddingRequest(
            Dialect,
            new GatewayEmbeddingRequest(input.ToJsonString()),
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
            var data = new JsonArray();
            for (var index = 0; index < providerResponse.Embeddings.Count; index++)
            {
                data.Add(new JsonObject
                {
                    ["object"] = "embedding",
                    ["index"] = index,
                    ["embedding"] = providerResponse.Embeddings[index]?.DeepClone()
                });
            }

            responseBody = new JsonObject
            {
                ["object"] = "list",
                ["data"] = data,
                ["model"] = virtualModel.Name,
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = providerResponse.PromptTokens,
                    ["total_tokens"] = providerResponse.PromptTokens
                }
            };
        }

        response.ContentType = "application/json";
        await response.WriteAsync(responseBody.ToJsonString(), cancellationToken);
    }
}
