using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatProviderRequestAdapter : IChatProviderRequestAdapter
{
    public ProviderType ProviderType => ProviderType.OpenAI;

    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;

    public HttpRequestMessage CreateRequest(
        DecodedChatRequest decoded,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        var body = decoded.SourceDialect == Dialect
            ? decoded.OriginalBody.DeepClone().AsObject()
            : BuildCrossDialectBody(decoded);

        body["model"] = backend.UnderlyingModelName;
        body["stream"] = decoded.Request.Stream;
        if (decoded.Request.Stream)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };

        ApplyOptions(body, decoded.Request.Options, virtualModel);
        NormalizeMessages(body);

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend.Provider!.BaseUrl.TrimEnd('/')}/v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private static JsonObject BuildCrossDialectBody(DecodedChatRequest decoded)
    {
        var body = new JsonObject
        {
            ["messages"] = ChatProviderEncoding.BuildOpenAiMessages(decoded)
        };
        if (decoded.Request.Tools.Count > 0)
            body["tools"] = ChatProviderEncoding.BuildOpenAiTools(decoded.Request.Tools);
        if (decoded.Request.ToolChoiceJson != null)
        {
            try { body["tool_choice"] = JsonNode.Parse(decoded.Request.ToolChoiceJson); }
            catch (System.Text.Json.JsonException) { /* Ignore an invalid optional hint. */ }
        }
        return body;
    }

    private static void ApplyOptions(JsonObject body, GatewayChatOptions options, VirtualModel virtualModel)
    {
        if (options.Temperature.HasValue) body["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue) body["top_p"] = options.TopP.Value;
        if (options.MaxTokens.HasValue) body["max_tokens"] = options.MaxTokens.Value;
        if (virtualModel.Temperature.HasValue) body["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) body["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.NumPredict.HasValue) body["max_tokens"] = virtualModel.NumPredict.Value;

        var thinking = virtualModel.Thinking ?? options.Thinking;
        if (thinking.HasValue)
            body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking.Value };
    }

    private static void NormalizeMessages(JsonObject body)
    {
        if (body["messages"] is not JsonArray messages) return;
        foreach (var node in messages)
        {
            if (node is not JsonObject message) continue;
            message["content"] ??= string.Empty;
            message["role"] ??= "user";
        }
    }
}
