using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public sealed class OllamaEmbeddingProviderAdapter : IEmbeddingProviderAdapter
{
    public ProviderType ProviderType => ProviderType.Ollama;

    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

    public HttpRequestMessage CreateRequest(
        DecodedEmbeddingRequest request,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        var body = request.SourceDialect == Dialect
            ? request.OriginalBody.DeepClone().AsObject()
            : new JsonObject { ["input"] = JsonNode.Parse(request.Request.InputJson) };
        body["model"] = backend.UnderlyingModelName;

        if (request.SourceDialect == Dialect)
        {
            body["keep_alive"] = backend.Provider!.KeepAlive;
        }

        ApplyOptions(body, virtualModel);

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend.Provider!.BaseUrl.TrimEnd('/')}/api/embed")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    public GatewayEmbeddingProviderResponse DecodeResponse(string responseBody)
    {
        var body = JsonNode.Parse(responseBody)?.AsObject()
                   ?? throw new InvalidOperationException("Ollama embedding response is not a JSON object.");
        var embeddings = body["embeddings"]?.DeepClone().AsArray() ?? new JsonArray();
        var promptTokens = body["prompt_eval_count"]?.GetValue<long>() ?? 0;
        return new GatewayEmbeddingProviderResponse(Dialect, body, embeddings, promptTokens);
    }

    private static void ApplyOptions(JsonObject body, VirtualModel virtualModel)
    {
        if (!virtualModel.NumCtx.HasValue &&
            !virtualModel.Temperature.HasValue &&
            !virtualModel.TopP.HasValue &&
            !virtualModel.TopK.HasValue &&
            !virtualModel.RepeatPenalty.HasValue)
        {
            return;
        }

        var options = body["options"]?.AsObject() ?? new JsonObject();
        if (virtualModel.NumCtx.HasValue) options["num_ctx"] = virtualModel.NumCtx.Value;
        if (virtualModel.Temperature.HasValue) options["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) options["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.TopK.HasValue) options["top_k"] = virtualModel.TopK.Value;
        if (virtualModel.RepeatPenalty.HasValue) options["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
        body["options"] = options;
    }
}
