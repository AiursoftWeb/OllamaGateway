using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiResponsesProviderRequestAdapter : IChatProviderRequestAdapter
{
    public BackendProtocol Protocol => BackendProtocol.OpenAiResponses;

    public HttpRequestMessage CreateRequest(
        DecodedChatRequest decoded,
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        var sameDialect = decoded.SourceDialect == ProtocolDialect.OpenAiResponses;
        var body = sameDialect
            ? decoded.OriginalBody.DeepClone().AsObject()
            : BuildCrossDialectBody(decoded);

        body["model"] = backend.UnderlyingModelName;
        body["stream"] = decoded.Request.Stream;
        // This first gateway implementation is deliberately stateless. Prevent
        // an omitted store option from inheriting the upstream default of true.
        body["store"] = false;
        if (sameDialect)
            ApplyVirtualModelOptions(body, virtualModel);
        else
            ApplyOptions(body, decoded.Request.Options, virtualModel);

        return new HttpRequestMessage(
            HttpMethod.Post,
            $"{backend.Provider!.BaseUrl.TrimEnd('/')}/v1/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private static JsonObject BuildCrossDialectBody(DecodedChatRequest decoded)
    {
        var body = new JsonObject
        {
            ["input"] = BuildInput(decoded.Request.Messages)
        };

        var instructions = decoded.Request.Instructions;
        if (string.IsNullOrWhiteSpace(instructions))
        {
            instructions = string.Join("\n\n", decoded.Request.Messages
                .Where(message => message.Role is "system" or "developer")
                .SelectMany(message => message.Content.OfType<GatewayTextContent>())
                .Select(part => part.Text));
        }
        if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;

        if (decoded.Request.Tools.Count > 0)
            body["tools"] = BuildTools(decoded.Request.Tools);
        if (decoded.Request.ToolChoice != null)
            body["tool_choice"] = ChatProviderEncoding.EncodeResponsesToolChoice(decoded.Request.ToolChoice);
        return body;
    }

    private static JsonArray BuildInput(IReadOnlyList<GatewayChatMessage> messages)
    {
        var result = new JsonArray();
        foreach (var message in messages)
        {
            if (message.Role is "system" or "developer") continue;

            var ordinary = message.Content
                .Where(part => part is GatewayTextContent or GatewayImageContent)
                .ToList();
            if (ordinary.Count > 0)
            {
                var item = new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = string.IsNullOrWhiteSpace(message.Role) ? "user" : message.Role,
                    ["content"] = BuildContent(ordinary, message.Role)
                };
                result.Add(item);
            }

            foreach (var call in message.Content.OfType<GatewayToolCallContent>())
            {
                result.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call.Id,
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson
                });
            }

            foreach (var output in message.Content.OfType<GatewayToolResultContent>())
            {
                result.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = output.ToolCallId,
                    ["output"] = output.IsError ? $"Error: {output.Content}" : output.Content
                });
            }
        }
        return result;
    }

    private static JsonNode BuildContent(IReadOnlyList<GatewayContentPart> parts, string role)
    {
        var images = parts.OfType<GatewayImageContent>().ToList();
        var text = string.Concat(parts.OfType<GatewayTextContent>().Select(part => part.Text));
        if (images.Count == 0) return JsonValue.Create(text);

        var content = new JsonArray();
        if (!string.IsNullOrEmpty(text))
        {
            content.Add(new JsonObject
            {
                ["type"] = role == "assistant" ? "output_text" : "input_text",
                ["text"] = text
            });
        }
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = image.IsUrl
                    ? image.Data
                    : $"data:{image.MediaType ?? "image/png"};base64,{image.Data}"
            });
        }
        return content;
    }

    private static JsonArray BuildTools(IReadOnlyList<GatewayToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            JsonNode? schema;
            try { schema = JsonNode.Parse(tool.InputSchemaJson); }
            catch (System.Text.Json.JsonException) { schema = new JsonObject(); }
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = schema ?? new JsonObject()
            });
        }
        return result;
    }

    private static void ApplyOptions(JsonObject body, GatewayChatOptions options, VirtualModel virtualModel)
    {
        if (options.Temperature.HasValue) body["temperature"] = options.Temperature.Value;
        if (options.TopP.HasValue) body["top_p"] = options.TopP.Value;
        if (options.MaxTokens.HasValue) body["max_output_tokens"] = options.MaxTokens.Value;
        if (virtualModel.Temperature.HasValue) body["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) body["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.NumPredict.HasValue) body["max_output_tokens"] = virtualModel.NumPredict.Value;
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };
        if (!string.IsNullOrWhiteSpace(options.StructuredOutputJson))
        {
            try
            {
                body["text"] = new JsonObject { ["format"] = JsonNode.Parse(options.StructuredOutputJson) };
            }
            catch (System.Text.Json.JsonException) { /* Invalid optional format was already unusable. */ }
        }
    }

    private static void ApplyVirtualModelOptions(JsonObject body, VirtualModel virtualModel)
    {
        if (virtualModel.Temperature.HasValue) body["temperature"] = virtualModel.Temperature.Value;
        if (virtualModel.TopP.HasValue) body["top_p"] = virtualModel.TopP.Value;
        if (virtualModel.NumPredict.HasValue) body["max_output_tokens"] = virtualModel.NumPredict.Value;
    }
}
