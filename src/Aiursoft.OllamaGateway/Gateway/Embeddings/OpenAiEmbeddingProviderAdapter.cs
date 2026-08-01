using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public sealed class OpenAiEmbeddingProviderAdapter : IEmbeddingProviderAdapter
{
    public ProviderType ProviderType => ProviderType.OpenAI;

    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;

    public HttpRequestMessage CreateRequest(
        DecodedEmbeddingRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        _ = virtualModel;
        var body = request.SourceDialect == Dialect
            ? request.OriginalBody.DeepClone().AsObject()
            : new JsonObject { ["input"] = JsonNode.Parse(request.Request.InputJson) };
        body["model"] = backend.UnderlyingModelName;

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend.Provider!.BaseUrl.TrimEnd('/')}/v1/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    public GatewayEmbeddingProviderResponse DecodeResponse(string responseBody)
    {
        var body = JsonNode.Parse(responseBody)?.AsObject()
                   ?? throw new InvalidOperationException("OpenAI embedding response is not a JSON object.");
        var embeddings = new JsonArray();
        if (body["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                embeddings.Add(item?["embedding"]?.DeepClone());
            }
        }

        var promptTokens = body["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
        return new GatewayEmbeddingProviderResponse(Dialect, body, embeddings, promptTokens);
    }
}
