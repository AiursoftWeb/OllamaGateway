using System.Globalization;
using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

internal static class ChatRequestDecoding
{
    public static JsonObject ParseBody(string body)
    {
        return JsonNode.Parse(body)?.AsObject()
               ?? throw new InvalidOperationException("Chat request body must be a JSON object.");
    }

    public static string StringValue(JsonNode? node, string fallback = "")
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return node?.ToString() ?? fallback;
    }

    public static double? DoubleValue(JsonNode? node)
    {
        return double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static int? IntValue(JsonNode? node)
    {
        return int.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static bool? BoolValue(JsonNode? node)
    {
        return bool.TryParse(node?.ToString(), out var value) ? value : null;
    }

    public static IReadOnlyList<GatewayToolDefinition> DecodeOpenAiTools(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray tools) return [];

        var result = new List<GatewayToolDefinition>();
        foreach (var tool in tools)
        {
            var function = tool?["function"];
            var name = StringValue(function?["name"]);
            if (string.IsNullOrEmpty(name)) continue;
            result.Add(new GatewayToolDefinition(
                name,
                StringValue(function?["description"]),
                function?["parameters"]?.ToJsonString() ?? "{}"));
        }

        return result;
    }

    public static IReadOnlyList<GatewayContentPart> DecodeOpenAiMessage(JsonObject message)
    {
        var result = new List<GatewayContentPart>();
        var role = StringValue(message["role"], "user");
        var content = message["content"];

        if (content is JsonArray contentArray)
        {
            foreach (var item in contentArray)
            {
                var type = StringValue(item?["type"]);
                if (type is "text" or "input_text")
                {
                    result.Add(new GatewayTextContent(StringValue(item?["text"] ?? item?["input_text"])));
                }
                else if (type is "image_url" or "input_image")
                {
                    var url = StringValue(item?["image_url"]?["url"] ?? item?["image_url"] ?? item?["url"]);
                    var (data, mediaType, isUrl) = DecodeImage(url);
                    result.Add(new GatewayImageContent(data, mediaType, isUrl));
                }
                else if (item != null)
                {
                    result.Add(new GatewayOpaqueContent(
                        ProtocolDialect.OpenAiChatCompletions,
                        type,
                        item.ToJsonString()));
                }
            }
        }
        else if (content != null)
        {
            var text = StringValue(content);
            result.Add(role == "tool"
                ? new GatewayToolResultContent(StringValue(message["tool_call_id"]), text)
                : new GatewayTextContent(text));
        }

        var reasoning = StringValue(message["reasoning_content"]);
        if (!string.IsNullOrEmpty(reasoning)) result.Add(new GatewayReasoningContent(reasoning));

        if (message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var toolCall in toolCalls)
            {
                var function = toolCall?["function"];
                result.Add(new GatewayToolCallContent(
                    StringValue(toolCall?["id"]),
                    StringValue(function?["name"]),
                    function?["arguments"] is JsonValue
                        ? StringValue(function["arguments"], "{}")
                        : function?["arguments"]?.ToJsonString() ?? "{}"));
            }
        }

        return result;
    }

    public static (string Data, string? MediaType, bool IsUrl) DecodeImage(string source)
    {
        if (!source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (source, null, true);

        var comma = source.IndexOf(',');
        if (comma < 0) return (source, null, false);
        var metadata = source["data:".Length..comma];
        var semicolon = metadata.IndexOf(';');
        var mediaType = semicolon < 0 ? metadata : metadata[..semicolon];
        return (source[(comma + 1)..], string.IsNullOrWhiteSpace(mediaType) ? null : mediaType, false);
    }
}
