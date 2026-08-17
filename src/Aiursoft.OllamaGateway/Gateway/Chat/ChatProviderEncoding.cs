using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

internal static class ChatProviderEncoding
{
    public static JsonArray BuildOpenAiMessages(DecodedChatRequest decoded)
    {
        var result = new JsonArray();
        foreach (var message in decoded.Request.Messages)
        {
            // Anthropic carries tool results in user messages, while Ollama and OpenAI
            // use tool messages. Encode the semantic content independently of its
            // source role so the tool_call_id survives every cross-dialect route.
            if (message.Content.Any(part => part is GatewayToolResultContent))
            {
                var ordinaryParts = new List<GatewayContentPart>();
                foreach (var part in message.Content)
                {
                    if (part is not GatewayToolResultContent toolResult)
                    {
                        ordinaryParts.Add(part);
                        continue;
                    }

                    if (ordinaryParts.Count > 0)
                    {
                        AddOpenAiMessage(result, message.Role, ordinaryParts, decoded.SourceDialect);
                        ordinaryParts.Clear();
                    }
                    result.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolResult.ToolCallId,
                        ["content"] = toolResult.IsError ? $"Error: {toolResult.Content}" : toolResult.Content
                    });
                }
                if (ordinaryParts.Count > 0)
                    AddOpenAiMessage(result, message.Role, ordinaryParts, decoded.SourceDialect);
                continue;
            }

            AddOpenAiMessage(result, message.Role, message.Content, decoded.SourceDialect);
        }

        return result;
    }

    public static JsonArray BuildOllamaMessages(DecodedChatRequest decoded)
    {
        var openAiMessages = BuildOpenAiMessages(decoded);
        foreach (var node in openAiMessages)
        {
            if (node is not JsonObject message) continue;
            if (message["content"] is JsonArray content)
            {
                var text = new System.Text.StringBuilder();
                var images = new JsonArray();
                foreach (var item in content)
                {
                    if (item?["type"]?.ToString() == "text")
                        text.Append(item["text"]);
                    else if (item?["type"]?.ToString() == "image_url")
                    {
                        var url = item["image_url"]?["url"]?.ToString() ?? string.Empty;
                        images.Add(ChatRequestDecoding.DecodeImage(url).Data);
                    }
                }
                message["content"] = text.ToString();
                if (images.Count > 0) message["images"] = images;
            }

            if (message["tool_calls"] is not JsonArray toolCalls) continue;
            foreach (var toolCall in toolCalls)
            {
                if (toolCall?["function"] is not JsonObject function) continue;
                var arguments = function["arguments"]?.ToString();
                try
                {
                    function["arguments"] = JsonNode.Parse(arguments ?? "{}") ?? new JsonObject();
                }
                catch (System.Text.Json.JsonException)
                {
                    function["arguments"] = new JsonObject();
                }
            }
        }

        return openAiMessages;
    }

    public static JsonArray BuildOpenAiTools(IReadOnlyList<GatewayToolDefinition> tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools)
        {
            JsonNode? schema;
            try
            {
                schema = JsonNode.Parse(tool.InputSchemaJson);
            }
            catch (System.Text.Json.JsonException)
            {
                schema = new JsonObject();
            }

            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = schema ?? new JsonObject()
                }
            });
        }

        return result;
    }

    public static GatewayToolChoice? DecodeOpenAiToolChoice(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonValue)
            return ParseMode(ChatRequestDecoding.StringValue(node));
        var type = ChatRequestDecoding.StringValue(node["type"]);
        return type == "function"
            ? new GatewayToolChoice(
                GatewayToolChoiceMode.Function,
                ChatRequestDecoding.StringValue(node["function"]?["name"] ?? node["name"]))
            : ParseMode(type);
    }

    public static GatewayToolChoice? DecodeAnthropicToolChoice(JsonNode? node)
    {
        if (node == null) return null;
        var type = ChatRequestDecoding.StringValue(node["type"]);
        return type switch
        {
            "auto" => new GatewayToolChoice(GatewayToolChoiceMode.Auto),
            "none" => new GatewayToolChoice(GatewayToolChoiceMode.None),
            "any" => new GatewayToolChoice(GatewayToolChoiceMode.Required),
            "tool" => new GatewayToolChoice(
                GatewayToolChoiceMode.Function,
                ChatRequestDecoding.StringValue(node["name"])),
            _ => null
        };
    }

    public static JsonNode EncodeOpenAiToolChoice(GatewayToolChoice choice) => choice.Mode switch
    {
        GatewayToolChoiceMode.Auto => JsonValue.Create("auto"),
        GatewayToolChoiceMode.None => JsonValue.Create("none"),
        GatewayToolChoiceMode.Required => JsonValue.Create("required"),
        GatewayToolChoiceMode.Function => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = choice.FunctionName }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, null)
    };

    public static JsonNode EncodeResponsesToolChoice(GatewayToolChoice choice) => choice.Mode switch
    {
        GatewayToolChoiceMode.Auto => JsonValue.Create("auto"),
        GatewayToolChoiceMode.None => JsonValue.Create("none"),
        GatewayToolChoiceMode.Required => JsonValue.Create("required"),
        GatewayToolChoiceMode.Function => new JsonObject
        {
            ["type"] = "function",
            ["name"] = choice.FunctionName
        },
        _ => throw new ArgumentOutOfRangeException(nameof(choice), choice, null)
    };

    private static GatewayToolChoice? ParseMode(string mode) => mode switch
    {
        "auto" => new GatewayToolChoice(GatewayToolChoiceMode.Auto),
        "none" => new GatewayToolChoice(GatewayToolChoiceMode.None),
        "required" => new GatewayToolChoice(GatewayToolChoiceMode.Required),
        _ => null
    };

    private static void AddOpenAiMessage(
        JsonArray target,
        string role,
        IReadOnlyList<GatewayContentPart> parts,
        ProtocolDialect sourceDialect)
    {
        if (parts.Count == 0)
        {
            target.Add(new JsonObject { ["role"] = role, ["content"] = string.Empty });
            return;
        }

        var message = new JsonObject { ["role"] = string.IsNullOrEmpty(role) ? "user" : role };
        var textParts = parts.OfType<GatewayTextContent>().Select(part => part.Text).ToList();
        var images = parts.OfType<GatewayImageContent>().ToList();
        var separator = sourceDialect == ProtocolDialect.AnthropicMessages ? "\n" : string.Empty;
        var text = string.Join(separator, textParts);

        if (images.Count > 0)
        {
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text }
            };
            foreach (var image in images)
            {
                var url = image.IsUrl
                    ? image.Data
                    : $"data:{image.MediaType ?? "image/png"};base64,{image.Data}";
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = url }
                });
            }
            message["content"] = content;
        }
        else
        {
            message["content"] = text;
        }

        var reasoning = string.Join(separator, parts.OfType<GatewayReasoningContent>().Select(part => part.Text));
        if (!string.IsNullOrEmpty(reasoning)) message["reasoning_content"] = reasoning;

        var toolCalls = parts.OfType<GatewayToolCallContent>().ToList();
        if (toolCalls.Count > 0)
        {
            var array = new JsonArray();
            foreach (var toolCall in toolCalls)
            {
                array.Add(new JsonObject
                {
                    ["id"] = toolCall.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = toolCall.Name,
                        ["arguments"] = toolCall.ArgumentsJson
                    }
                });
            }
            message["tool_calls"] = array;
        }

        target.Add(message);
    }
}
