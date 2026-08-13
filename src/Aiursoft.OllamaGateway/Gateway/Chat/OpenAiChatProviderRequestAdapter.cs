using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatProviderRequestAdapter : IChatProviderRequestAdapter
{
    public BackendProtocol Protocol => BackendProtocol.OpenAiChatCompletions;

    private const ProtocolDialect Dialect = ProtocolDialect.OpenAiChatCompletions;

    public HttpRequestMessage CreateRequest(
        DecodedChatRequest decoded,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        var sameDialect = decoded.SourceDialect == Dialect;
        var body = sameDialect
            ? decoded.OriginalBody.DeepClone().AsObject()
            : BuildCrossDialectBody(decoded);

        body["model"] = backend.UnderlyingModelName;
        body["stream"] = decoded.Request.Stream;
        if (decoded.Request.Stream)
        {
            var streamOptions = body["stream_options"] as JsonObject ?? new JsonObject();
            streamOptions["include_usage"] = true;
            body["stream_options"] = streamOptions;
        }

        if (sameDialect)
            ApplyVirtualModelOptions(body, virtualModel);
        else
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
        if (decoded.Request.ToolChoice != null)
            body["tool_choice"] = ChatProviderEncoding.EncodeOpenAiToolChoice(decoded.Request.ToolChoice);
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
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            body["reasoning_effort"] = options.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(options.StructuredOutputJson))
        {
            try
            {
                var format = JsonNode.Parse(options.StructuredOutputJson);
                if (format?["type"]?.ToString() == "json_schema")
                {
                    body["response_format"] = new JsonObject
                    {
                        ["type"] = "json_schema",
                        ["json_schema"] = new JsonObject
                        {
                            ["name"] = format["name"]?.DeepClone() ?? JsonValue.Create("response"),
                            ["schema"] = format["schema"]?.DeepClone() ?? new JsonObject(),
                            ["strict"] = format["strict"]?.DeepClone()
                        }
                    };
                }
                else if (format != null)
                {
                    body["response_format"] = format;
                }
            }
            catch (System.Text.Json.JsonException) { /* Ignore an invalid optional format. */ }
        }

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

    private static void ApplyVirtualModelOptions(JsonObject body, VirtualModel virtualModel)
    {
        if (virtualModel.Temperature.HasValue) body["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) body["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.NumPredict.HasValue)
        {
            if (body.ContainsKey("max_completion_tokens")) body["max_completion_tokens"] = virtualModel.NumPredict.Value;
            else body["max_tokens"] = virtualModel.NumPredict.Value;
        }
        if (!virtualModel.Thinking.HasValue) return;

        var templateArguments = body["chat_template_kwargs"] as JsonObject ?? new JsonObject();
        templateArguments["enable_thinking"] = virtualModel.Thinking.Value;
        body["chat_template_kwargs"] = templateArguments;
    }
}
