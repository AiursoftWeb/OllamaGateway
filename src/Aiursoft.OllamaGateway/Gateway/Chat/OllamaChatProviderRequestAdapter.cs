using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OllamaChatProviderRequestAdapter : IChatProviderRequestAdapter
{
    public ProviderType ProviderType => ProviderType.Ollama;

    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

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
        if (decoded.SourceDialect == Dialect)
            body["keep_alive"] ??= decoded.Request.Options.KeepAlive ?? backend.Provider!.KeepAlive;
        ApplyOptions(body, decoded.Request.Options, virtualModel);

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend.Provider!.BaseUrl.TrimEnd('/')}/api/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private static JsonObject BuildCrossDialectBody(DecodedChatRequest decoded)
    {
        var body = new JsonObject
        {
            ["messages"] = ChatProviderEncoding.BuildOllamaMessages(decoded)
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
        JsonObject optionBody;
        try
        {
            optionBody = body["options"]?.AsObject() ?? new JsonObject();
        }
        catch (InvalidOperationException)
        {
            optionBody = new JsonObject();
        }

        if (options.Temperature.HasValue) optionBody["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue) optionBody["top_p"] = options.TopP.Value;
        if (options.TopK.HasValue) optionBody["top_k"] = options.TopK.Value;
        if (options.MaxTokens.HasValue) optionBody["num_predict"] = options.MaxTokens.Value;
        if (options.ContextSize.HasValue) optionBody["num_ctx"] = options.ContextSize.Value;
        if (options.RepeatPenalty.HasValue) optionBody["repeat_penalty"] = options.RepeatPenalty.Value;

        if (virtualModel.Temperature.HasValue) optionBody["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) optionBody["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.TopK.HasValue) optionBody["top_k"] = virtualModel.TopK.Value;
        if (virtualModel.NumPredict.HasValue) optionBody["num_predict"] = virtualModel.NumPredict.Value;
        if (virtualModel.NumCtx.HasValue) optionBody["num_ctx"] = virtualModel.NumCtx.Value;
        if (virtualModel.RepeatPenalty.HasValue) optionBody["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
        if (optionBody.Count > 0) body["options"] = optionBody;

        var thinking = virtualModel.Thinking ?? options.Thinking;
        if (thinking.HasValue) body["think"] = thinking.Value;
    }
}
