using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Gateway.Execution;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiResponsesRequestDecoder : IChatRequestDecoder
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiResponses;

    public DecodedChatRequest Decode(string body)
    {
        var root = ChatRequestDecoding.ParseBody(body);
        var messages = DecodeInput(root["input"]);
        var instructions = ChatRequestDecoding.StringValue(root["instructions"], null!);
        if (!string.IsNullOrWhiteSpace(instructions))
            messages.Insert(0, new GatewayChatMessage("system", [new GatewayTextContent(instructions)]));

        var tools = new List<GatewayToolDefinition>();
        var hasNativeTools = false;
        if (root["tools"] is JsonArray toolArray)
        {
            foreach (var node in toolArray)
            {
                if (node is not JsonObject tool) continue;
                var type = ChatRequestDecoding.StringValue(tool["type"]);
                if (type != "function")
                {
                    hasNativeTools = true;
                    continue;
                }

                var name = ChatRequestDecoding.StringValue(tool["name"] ?? tool["function"]?["name"]);
                if (string.IsNullOrWhiteSpace(name)) continue;
                tools.Add(new GatewayToolDefinition(
                    name,
                    ChatRequestDecoding.StringValue(tool["description"] ?? tool["function"]?["description"]),
                    (tool["parameters"] ?? tool["function"]?["parameters"])?.ToJsonString() ?? "{}"));
            }
        }

        var streaming = ChatRequestDecoding.BoolValue(root["stream"]) ?? false;
        var requiresNativeResponses = root["previous_response_id"] != null ||
                                      root["conversation"] != null ||
                                      ChatRequestDecoding.BoolValue(root["background"]) == true ||
                                      ChatRequestDecoding.BoolValue(root["store"]) == true;
        var structuredOutput = root["text"]?["format"];
        var hasOpaqueContent = messages
            .SelectMany(message => message.Content)
            .Any(part => part is GatewayOpaqueContent);
        var requiredCapabilities = ChatRequestCapabilities.Infer(
            streaming,
            messages,
            tools,
            structuredOutput != null,
            hasNativeTools,
            requiresNativeResponses,
            root["reasoning"] != null);
        if (hasOpaqueContent)
            requiredCapabilities |= GatewayCapability.OpenAiResponsesPassthrough;
        // Prefer native Responses for every Responses request. If no such backend
        // exists, representable stateless requests may still be translated.
        var preferredCapabilities = GatewayCapability.OpenAiResponsesPassthrough;
        var request = new GatewayChatRequest(
            streaming,
            messages,
            new GatewayChatOptions(
                Temperature: ChatRequestDecoding.DoubleValue(root["temperature"]),
                TopP: ChatRequestDecoding.DoubleValue(root["top_p"]),
                MaxTokens: ChatRequestDecoding.IntValue(root["max_output_tokens"]),
                ReasoningEffort: ChatRequestDecoding.StringValue(root["reasoning"]?["effort"], null!),
                StructuredOutputJson: structuredOutput?.ToJsonString()),
            tools,
            ChatProviderEncoding.DecodeOpenAiToolChoice(root["tool_choice"]),
            instructions,
            requiredCapabilities,
            preferredCapabilities);

        return new DecodedChatRequest(Dialect, request, root);
    }

    private static List<GatewayChatMessage> DecodeInput(JsonNode? input)
    {
        if (input is JsonValue)
            return [new GatewayChatMessage("user", [new GatewayTextContent(ChatRequestDecoding.StringValue(input))])];

        var result = new List<GatewayChatMessage>();
        if (input is not JsonArray array) return result;
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var type = ChatRequestDecoding.StringValue(item["type"], "message");
            switch (type)
            {
                case "message":
                case "easy_input_message":
                    result.Add(new GatewayChatMessage(
                        ChatRequestDecoding.StringValue(item["role"], "user"),
                        DecodeMessageContent(item["content"])));
                    break;
                case "function_call":
                    result.Add(new GatewayChatMessage("assistant",
                    [
                        new GatewayToolCallContent(
                            ChatRequestDecoding.StringValue(item["call_id"] ?? item["id"]),
                            ChatRequestDecoding.StringValue(item["name"]),
                            JsonArguments(item["arguments"]))
                    ]));
                    break;
                case "function_call_output":
                    result.Add(new GatewayChatMessage("user",
                    [
                        new GatewayToolResultContent(
                            ChatRequestDecoding.StringValue(item["call_id"]),
                            ExtractOutput(item["output"]))
                    ]));
                    break;
                case "reasoning":
                    var summary = ExtractReasoning(item);
                    if (!string.IsNullOrEmpty(summary))
                        result.Add(new GatewayChatMessage("assistant", [new GatewayReasoningContent(summary)]));
                    break;
                default:
                    result.Add(new GatewayChatMessage("user",
                    [
                        new GatewayOpaqueContent(ProtocolDialect.OpenAiResponses, type, item.ToJsonString())
                    ]));
                    break;
            }
        }

        return result;
    }

    private static IReadOnlyList<GatewayContentPart> DecodeMessageContent(JsonNode? content)
    {
        if (content is JsonValue)
            return [new GatewayTextContent(ChatRequestDecoding.StringValue(content))];

        var parts = new List<GatewayContentPart>();
        if (content is not JsonArray array) return parts;
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var type = ChatRequestDecoding.StringValue(item["type"]);
            switch (type)
            {
                case "input_text":
                case "output_text":
                case "text":
                    parts.Add(new GatewayTextContent(ChatRequestDecoding.StringValue(item["text"])));
                    break;
                case "input_image":
                    var source = ChatRequestDecoding.StringValue(item["image_url"] ?? item["url"]);
                    var (data, mediaType, isUrl) = ChatRequestDecoding.DecodeImage(source);
                    parts.Add(new GatewayImageContent(data, mediaType, isUrl));
                    break;
                case "refusal":
                    parts.Add(new GatewayTextContent(ChatRequestDecoding.StringValue(item["refusal"])));
                    break;
                default:
                    parts.Add(new GatewayOpaqueContent(ProtocolDialect.OpenAiResponses, type, item.ToJsonString()));
                    break;
            }
        }
        return parts;
    }

    private static string JsonArguments(JsonNode? node)
    {
        if (node is JsonValue) return ChatRequestDecoding.StringValue(node, "{}");
        return node?.ToJsonString() ?? "{}";
    }

    private static string ExtractOutput(JsonNode? output)
    {
        if (output is JsonValue) return ChatRequestDecoding.StringValue(output);
        if (output is not JsonArray array) return output?.ToJsonString() ?? string.Empty;
        return string.Join("\n", array.Select(item =>
            ChatRequestDecoding.StringValue(item?["text"] ?? item?["output_text"] ?? item)));
    }

    private static string ExtractReasoning(JsonObject item)
    {
        if (item["summary"] is not JsonArray summary) return string.Empty;
        return string.Join("\n", summary.Select(part => ChatRequestDecoding.StringValue(part?["text"] ?? part)));
    }

}
