using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class AnthropicChatClientResponseWriter : IChatClientResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.AnthropicMessages;

    public async Task WriteTranslatedAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        bool streaming,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        if (streaming)
            await WriteStreamingAsync(events, virtualModel, response, cancellationToken);
        else
            await WriteBufferedAsync(events, virtualModel, response, cancellationToken);
    }

    private static async Task WriteStreamingAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var messageId = $"msg_{Guid.NewGuid():N}";
        await WriteEventAsync(response, "message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = virtualModel.Name,
                ["content"] = new JsonArray(),
                ["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0 }
            }
        }, cancellationToken);

        var nextBlockIndex = 0;
        int? thinkingIndex = null;
        int? textIndex = null;
        var openBlocks = new HashSet<int>();
        var toolBlocks = new Dictionary<int, int>();
        long outputTokens = 0;

        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayReasoningDelta reasoning:
                    if (thinkingIndex == null)
                    {
                        thinkingIndex = nextBlockIndex++;
                        openBlocks.Add(thinkingIndex.Value);
                        await WriteEventAsync(response, "content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = thinkingIndex.Value,
                            ["content_block"] = new JsonObject
                            {
                                ["type"] = "thinking",
                                ["thinking"] = string.Empty,
                                ["signature"] = Signature()
                            }
                        }, cancellationToken);
                    }
                    await WriteEventAsync(response, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = thinkingIndex.Value,
                        ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = reasoning.Text }
                    }, cancellationToken);
                    break;

                case GatewayTextDelta text:
                    if (thinkingIndex.HasValue && openBlocks.Remove(thinkingIndex.Value))
                        await WriteBlockStopAsync(response, thinkingIndex.Value, cancellationToken);
                    if (textIndex == null)
                    {
                        textIndex = nextBlockIndex++;
                        openBlocks.Add(textIndex.Value);
                        await WriteEventAsync(response, "content_block_start", new JsonObject
                        {
                            ["type"] = "content_block_start",
                            ["index"] = textIndex.Value,
                            ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = string.Empty }
                        }, cancellationToken);
                    }
                    await WriteEventAsync(response, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = textIndex.Value,
                        ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text.Text }
                    }, cancellationToken);
                    break;

                case GatewayToolCallStarted tool:
                    var blockIndex = nextBlockIndex++;
                    toolBlocks[tool.Index] = blockIndex;
                    openBlocks.Add(blockIndex);
                    await WriteEventAsync(response, "content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start",
                        ["index"] = blockIndex,
                        ["content_block"] = new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = string.IsNullOrEmpty(tool.Id) ? $"toolu_{Guid.NewGuid():N}" : tool.Id,
                            ["name"] = string.IsNullOrEmpty(tool.Name) ? "unknown" : tool.Name,
                            ["input"] = new JsonObject()
                        }
                    }, cancellationToken);
                    break;

                case GatewayToolArgumentsDelta arguments when toolBlocks.TryGetValue(arguments.Index, out var toolBlock):
                    await WriteEventAsync(response, "content_block_delta", new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = toolBlock,
                        ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments.JsonFragment }
                    }, cancellationToken);
                    break;

                case GatewayToolCallCompleted completed when toolBlocks.TryGetValue(completed.Index, out var completedBlock):
                    if (openBlocks.Remove(completedBlock))
                        await WriteBlockStopAsync(response, completedBlock, cancellationToken);
                    break;

                case GatewayUsageUpdated usage:
                    outputTokens = usage.CompletionTokens;
                    break;

                case GatewayResponseCompleted completed:
                    foreach (var openBlock in openBlocks.Order())
                        await WriteBlockStopAsync(response, openBlock, cancellationToken);
                    openBlocks.Clear();
                    await WriteEventAsync(response, "message_delta", new JsonObject
                    {
                        ["type"] = "message_delta",
                        ["delta"] = new JsonObject { ["stop_reason"] = StopReason(completed.FinishReason) },
                        ["usage"] = new JsonObject { ["output_tokens"] = outputTokens }
                    }, cancellationToken);
                    await WriteEventAsync(response, "message_stop", new JsonObject { ["type"] = "message_stop" }, cancellationToken);
                    break;
            }
        }
    }

    private static async Task WriteBufferedAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var state = new ChatResponseState();
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayTextDelta text: state.Text.Append(text.Text); break;
                case GatewayReasoningDelta reasoning: state.Reasoning.Append(reasoning.Text); break;
                case GatewayToolCallStarted started:
                    state.Tool(started.Index).Id = started.Id;
                    state.Tool(started.Index).Name = started.Name;
                    break;
                case GatewayToolArgumentsDelta arguments: state.Tool(arguments.Index).Arguments.Append(arguments.JsonFragment); break;
                case GatewayUsageUpdated usage:
                    state.PromptTokens = usage.PromptTokens;
                    state.CompletionTokens = usage.CompletionTokens;
                    break;
                case GatewayResponseCompleted completed: state.FinishReason = completed.FinishReason; break;
            }
        }

        var content = new JsonArray();
        if (state.Reasoning.Length > 0)
        {
            content.Add(new JsonObject
            {
                ["type"] = "thinking",
                ["thinking"] = state.Reasoning.ToString(),
                ["signature"] = Signature()
            });
        }
        if (state.Text.Length > 0)
            content.Add(new JsonObject { ["type"] = "text", ["text"] = state.Text.ToString() });
        foreach (var (_, tool) in state.Tools)
        {
            JsonNode? input;
            try { input = JsonNode.Parse(tool.Arguments.ToString()); }
            catch (System.Text.Json.JsonException) { input = new JsonObject(); }
            content.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = string.IsNullOrEmpty(tool.Id) ? $"toolu_{Guid.NewGuid():N}" : tool.Id,
                ["name"] = string.IsNullOrEmpty(tool.Name) ? "unknown" : tool.Name,
                ["input"] = input ?? new JsonObject()
            });
        }

        var root = new JsonObject
        {
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = virtualModel.Name,
            ["content"] = content,
            ["stop_reason"] = StopReason(state.FinishReason),
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = state.PromptTokens,
                ["output_tokens"] = state.CompletionTokens
            }
        };
        response.ContentType = "application/json";
        await response.WriteAsync(root.ToJsonString(), cancellationToken);
    }

    private static string StopReason(GatewayFinishReason reason)
    {
        return reason switch
        {
            GatewayFinishReason.Length => "max_tokens",
            GatewayFinishReason.ToolCalls => "tool_use",
            _ => "end_turn"
        };
    }

    private static string Signature() => Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    private static Task WriteBlockStopAsync(HttpResponse response, int index, CancellationToken cancellationToken)
    {
        return WriteEventAsync(response, "content_block_stop", new JsonObject
        {
            ["type"] = "content_block_stop",
            ["index"] = index
        }, cancellationToken);
    }

    private static async Task WriteEventAsync(
        HttpResponse response,
        string eventName,
        JsonObject data,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data.ToJsonString()}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
