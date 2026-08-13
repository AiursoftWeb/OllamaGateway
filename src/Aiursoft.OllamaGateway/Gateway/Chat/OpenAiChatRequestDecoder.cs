using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Gateway.Execution;

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

        var streaming = ChatRequestDecoding.BoolValue(root["stream"]) ?? false;
        var tools = ChatRequestDecoding.DecodeOpenAiTools(root["tools"]);
        var requiredCapabilities = ChatRequestCapabilities.Infer(
            streaming,
            messages,
            tools,
            structuredOutput: root["response_format"] != null,
            reasoningRequested: root["reasoning_effort"] != null);
        if (messages.SelectMany(message => message.Content).Any(part => part is GatewayOpaqueContent) ||
            HasUntranslatedTopLevelFields(root))
        {
            requiredCapabilities |= GatewayCapability.OpenAiChatPassthrough;
        }
        var request = new GatewayChatRequest(
            streaming,
            messages,
            new GatewayChatOptions(
                Temperature: ChatRequestDecoding.DoubleValue(root["temperature"]),
                TopP: ChatRequestDecoding.DoubleValue(root["top_p"]),
                MaxTokens: ChatRequestDecoding.IntValue(root["max_completion_tokens"] ?? root["max_tokens"]),
                Thinking: ChatRequestDecoding.BoolValue(root["chat_template_kwargs"]?["enable_thinking"]),
                ReasoningEffort: ChatRequestDecoding.StringValue(root["reasoning_effort"], null!),
                StructuredOutputJson: NormalizeResponseFormat(root["response_format"])),
            tools,
            ChatProviderEncoding.DecodeOpenAiToolChoice(root["tool_choice"]),
            RequiredCapabilities: requiredCapabilities);

        return new DecodedChatRequest(Dialect, request, root);
    }

    private static string? NormalizeResponseFormat(JsonNode? format)
    {
        if (format == null) return null;
        if (format["type"]?.ToString() != "json_schema" || format["json_schema"] == null)
            return format.ToJsonString();
        var schema = format["json_schema"]!;
        return new JsonObject
        {
            ["type"] = "json_schema",
            ["name"] = schema["name"]?.DeepClone(),
            ["schema"] = schema["schema"]?.DeepClone(),
            ["strict"] = schema["strict"]?.DeepClone()
        }.ToJsonString();
    }

    private static bool HasUntranslatedTopLevelFields(JsonObject root)
    {
        var translated = new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "messages", "stream", "stream_options", "temperature", "top_p", "max_tokens",
            "max_completion_tokens", "tools", "tool_choice", "response_format",
            "reasoning_effort", "chat_template_kwargs"
        };
        return root.Any(property => !translated.Contains(property.Key));
    }
}
