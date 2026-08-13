using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Gateway.Execution;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class AnthropicChatRequestDecoder : IChatRequestDecoder
{
    public ProtocolDialect Dialect => ProtocolDialect.AnthropicMessages;

    public DecodedChatRequest Decode(string body)
    {
        var root = ChatRequestDecoding.ParseBody(body);
        var messages = new List<GatewayChatMessage>();
        var systemParts = DecodeTextBlocks(root["system"]);
        if (systemParts.Count > 0)
            messages.Add(new GatewayChatMessage("system", systemParts));

        if (root["messages"] is JsonArray messageArray)
        {
            foreach (var node in messageArray)
            {
                if (node is not JsonObject message) continue;
                var role = ChatRequestDecoding.StringValue(message["role"], "user");
                var parts = DecodeContent(message["content"], role);
                if (role == "assistant" && parts.All(part => part is not GatewayReasoningContent))
                    ExtractThinkTags(parts);
                var topLevelReasoning = ChatRequestDecoding.StringValue(message["reasoning_content"]);
                if (!string.IsNullOrEmpty(topLevelReasoning) && parts.All(part => part is not GatewayReasoningContent))
                    parts.Add(new GatewayReasoningContent(topLevelReasoning));
                messages.Add(new GatewayChatMessage(role, parts));
            }
        }

        var tools = new List<GatewayToolDefinition>();
        if (root["tools"] is JsonArray toolsArray)
        {
            foreach (var tool in toolsArray)
            {
                var name = ChatRequestDecoding.StringValue(tool?["name"]);
                if (string.IsNullOrEmpty(name)) continue;
                tools.Add(new GatewayToolDefinition(
                    name,
                    ChatRequestDecoding.StringValue(tool?["description"]),
                    tool?["input_schema"]?.ToJsonString() ?? "{}"));
            }
        }

        var streaming = ChatRequestDecoding.BoolValue(root["stream"]) ?? false;
        var mergedMessages = MergeSystemMessages(messages);
        var requiredCapabilities = ChatRequestCapabilities.Infer(streaming, mergedMessages, tools);
        if (mergedMessages.SelectMany(message => message.Content).Any(part => part is GatewayOpaqueContent) ||
            HasUntranslatedTopLevelFields(root))
        {
            requiredCapabilities |= GatewayCapability.AnthropicPassthrough;
        }
        var request = new GatewayChatRequest(
            streaming,
            mergedMessages,
            new GatewayChatOptions(
                Temperature: ChatRequestDecoding.DoubleValue(root["temperature"]),
                TopP: ChatRequestDecoding.DoubleValue(root["top_p"]),
                MaxTokens: ChatRequestDecoding.IntValue(root["max_tokens"])),
            tools,
            ChatProviderEncoding.DecodeAnthropicToolChoice(root["tool_choice"]),
            RequiredCapabilities: requiredCapabilities);

        return new DecodedChatRequest(Dialect, request, root);
    }

    private static bool HasUntranslatedTopLevelFields(JsonObject root)
    {
        var translated = new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "messages", "system", "stream", "max_tokens", "temperature",
            "top_p", "tools", "tool_choice"
        };
        return root.Any(property => !translated.Contains(property.Key));
    }

    private static List<GatewayContentPart> DecodeContent(JsonNode? content, string role)
    {
        if (content is not JsonArray array)
        {
            return [new GatewayTextContent(ChatRequestDecoding.StringValue(content))];
        }

        var parts = new List<GatewayContentPart>();
        foreach (var item in array)
        {
            var type = ChatRequestDecoding.StringValue(item?["type"]);
            switch (type)
            {
                case "text":
                    parts.Add(new GatewayTextContent(ChatRequestDecoding.StringValue(item?["text"])));
                    break;
                case "thinking":
                    parts.Add(new GatewayReasoningContent(
                        ChatRequestDecoding.StringValue(item?["thinking"]),
                        ChatRequestDecoding.StringValue(item?["signature"], null!)));
                    break;
                case "tool_use":
                    parts.Add(new GatewayToolCallContent(
                        ChatRequestDecoding.StringValue(item?["id"]),
                        ChatRequestDecoding.StringValue(item?["name"]),
                        item?["input"]?.ToJsonString() ?? "{}"));
                    break;
                case "tool_result":
                    parts.Add(new GatewayToolResultContent(
                        ChatRequestDecoding.StringValue(item?["tool_use_id"]),
                        ExtractText(item?["content"]),
                        ChatRequestDecoding.BoolValue(item?["is_error"]) ?? false));
                    break;
                case "image":
                    var source = item?["source"];
                    var isUrl = ChatRequestDecoding.StringValue(source?["type"]) == "url";
                    parts.Add(new GatewayImageContent(
                        ChatRequestDecoding.StringValue(isUrl ? source?["url"] : source?["data"]),
                        ChatRequestDecoding.StringValue(source?["media_type"], null!),
                        isUrl));
                    break;
                default:
                    if (item != null)
                        parts.Add(new GatewayOpaqueContent(ProtocolDialect.AnthropicMessages, type, item.ToJsonString()));
                    break;
            }
        }

        if (parts.Count == 0 && role != "assistant") parts.Add(new GatewayTextContent(string.Empty));
        return parts;
    }

    private static List<GatewayContentPart> DecodeTextBlocks(JsonNode? content)
    {
        var text = ExtractText(content);
        return string.IsNullOrEmpty(text) ? [] : [new GatewayTextContent(text)];
    }

    private static string ExtractText(JsonNode? content)
    {
        if (content is not JsonArray array) return ChatRequestDecoding.StringValue(content);
        var parts = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue)
            {
                parts.Add(ChatRequestDecoding.StringValue(item));
                continue;
            }

            var type = ChatRequestDecoding.StringValue(item?["type"]);
            if (type == "text")
            {
                var text = ChatRequestDecoding.StringValue(item?["text"]);
                if (!string.IsNullOrEmpty(text)) parts.Add(text);
            }
            else if (type is "tool_use" or "tool_result")
            {
                parts.Add(ExtractText(item?["content"] ?? item?["input"]));
            }
        }
        return string.Join("\n", parts);
    }

    private static void ExtractThinkTags(List<GatewayContentPart> parts)
    {
        var textParts = parts.OfType<GatewayTextContent>().ToList();
        if (textParts.Count == 0) return;
        var fullText = string.Join("\n", textParts.Select(part => part.Text));
        var start = fullText.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return;

        var end = fullText.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        string reasoning;
        string visibleText;
        if (end > start)
        {
            reasoning = fullText[(start + "<think>".Length)..end].Trim();
            visibleText = fullText.Remove(start, end + "</think>".Length - start).Trim();
        }
        else
        {
            reasoning = fullText[(start + "<think>".Length)..].Trim();
            visibleText = fullText[..start].Trim();
        }

        parts.RemoveAll(part => part is GatewayTextContent);
        if (!string.IsNullOrEmpty(visibleText)) parts.Insert(0, new GatewayTextContent(visibleText));
        if (!string.IsNullOrEmpty(reasoning)) parts.Add(new GatewayReasoningContent(reasoning));
    }

    private static IReadOnlyList<GatewayChatMessage> MergeSystemMessages(List<GatewayChatMessage> messages)
    {
        var systemText = messages
            .Where(message => message.Role == "system")
            .SelectMany(message => message.Content.OfType<GatewayTextContent>())
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrEmpty(text))
            .ToList();
        var result = messages.Where(message => message.Role != "system").ToList();
        if (systemText.Count > 0)
            result.Insert(0, new GatewayChatMessage("system", [new GatewayTextContent(string.Join("\n\n", systemText))]));
        return result;
    }
}
