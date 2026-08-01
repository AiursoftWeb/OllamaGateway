using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatRequestDecoder : IChatRequestDecoder
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;

    public DecodedChatRequest Decode(string body)
    {
        var root = ChatRequestDecoding.ParseBody(body);
        var messages = new List<GatewayChatMessage>();
        if (root["messages"] is JsonArray messageArray)
        {
            foreach (var node in messageArray)
            {
                if (node is not JsonObject message) continue;
                messages.Add(new GatewayChatMessage(
                    ChatRequestDecoding.StringValue(message["role"], "user"),
                    ChatRequestDecoding.DecodeOpenAiMessage(message)));
            }
        }

        var request = new GatewayChatRequest(
            ChatRequestDecoding.BoolValue(root["stream"]) ?? false,
            messages,
            new GatewayChatOptions(
                Temperature: ChatRequestDecoding.DoubleValue(root["temperature"]),
                TopP: ChatRequestDecoding.DoubleValue(root["top_p"]),
                MaxTokens: ChatRequestDecoding.IntValue(root["max_tokens"]),
                Thinking: ChatRequestDecoding.BoolValue(root["chat_template_kwargs"]?["enable_thinking"])),
            ChatRequestDecoding.DecodeOpenAiTools(root["tools"]),
            root["tool_choice"]?.ToJsonString());

        return new DecodedChatRequest(Dialect, request, root);
    }
}
