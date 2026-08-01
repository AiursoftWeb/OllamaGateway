using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OllamaChatRequestDecoder : IChatRequestDecoder
{
    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

    public DecodedChatRequest Decode(string body)
    {
        var root = ChatRequestDecoding.ParseBody(body);
        var messages = new List<GatewayChatMessage>();
        if (root["messages"] is JsonArray messageArray)
        {
            foreach (var node in messageArray)
            {
                if (node is not JsonObject message) continue;
                var parts = new List<GatewayContentPart>();
                var role = ChatRequestDecoding.StringValue(message["role"], "user");
                var text = ChatRequestDecoding.StringValue(message["content"]);
                if (role == "tool")
                {
                    parts.Add(new GatewayToolResultContent(
                        ChatRequestDecoding.StringValue(message["tool_call_id"]),
                        text));
                }
                else
                {
                    parts.Add(new GatewayTextContent(text));
                }

                if (message["images"] is JsonArray images)
                {
                    foreach (var image in images)
                    {
                        parts.Add(new GatewayImageContent(ChatRequestDecoding.StringValue(image), null, false));
                    }
                }

                var reasoning = ChatRequestDecoding.StringValue(message["thinking"] ?? message["think"] ?? message["reasoning_content"]);
                if (!string.IsNullOrEmpty(reasoning)) parts.Add(new GatewayReasoningContent(reasoning));

                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var toolCall in toolCalls)
                    {
                        var function = toolCall?["function"];
                        parts.Add(new GatewayToolCallContent(
                            ChatRequestDecoding.StringValue(toolCall?["id"]),
                            ChatRequestDecoding.StringValue(function?["name"]),
                            function?["arguments"]?.ToJsonString() ?? "{}"));
                    }
                }

                messages.Add(new GatewayChatMessage(role, parts));
            }
        }

        var options = root["options"];
        var request = new GatewayChatRequest(
            ChatRequestDecoding.BoolValue(root["stream"]) ?? false,
            messages,
            new GatewayChatOptions(
                Temperature: ChatRequestDecoding.DoubleValue(options?["temperature"]),
                TopP: ChatRequestDecoding.DoubleValue(options?["top_p"]),
                TopK: ChatRequestDecoding.IntValue(options?["top_k"]),
                MaxTokens: ChatRequestDecoding.IntValue(options?["num_predict"]),
                ContextSize: ChatRequestDecoding.IntValue(options?["num_ctx"]),
                RepeatPenalty: ChatRequestDecoding.DoubleValue(options?["repeat_penalty"]),
                Thinking: ChatRequestDecoding.BoolValue(root["think"]),
                KeepAlive: ChatRequestDecoding.StringValue(root["keep_alive"], null!)),
            ChatRequestDecoding.DecodeOpenAiTools(root["tools"]),
            root["tool_choice"]?.ToJsonString());

        return new DecodedChatRequest(Dialect, request, root);
    }
}
