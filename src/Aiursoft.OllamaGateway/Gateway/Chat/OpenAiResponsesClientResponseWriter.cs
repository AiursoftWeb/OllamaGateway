using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiResponsesClientResponseWriter : IChatClientResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiResponses;

    public Task WriteTranslatedAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        bool streaming,
        HttpResponse response,
        CancellationToken cancellationToken) => streaming
            ? WriteStreamingAsync(events, virtualModel, response, cancellationToken)
            : WriteBufferedAsync(events, virtualModel, response, cancellationToken);

    private static async Task WriteBufferedAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        var state = await AccumulateAsync(events, cancellationToken);
        response.ContentType = "application/json";
        await response.WriteAsync(BuildResponse(state, virtualModel.Name).ToJsonString(), cancellationToken);
    }

    private static async Task WriteStreamingAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        VirtualModel virtualModel,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        var state = new ChatResponseState { ResponseId = $"resp_{Guid.NewGuid():N}" };
        var sequence = 0;
        var created = false;
        var textStarted = false;
        var reasoningStarted = false;
        var completedTools = new HashSet<int>();

        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayResponseStarted started:
                    state.ResponseId = string.IsNullOrWhiteSpace(started.ResponseId)
                        ? state.ResponseId
                        : NormalizeResponseId(started.ResponseId);
                    state.CreatedAt = started.CreatedAtUnixSeconds;
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    break;
                case GatewayTextDelta text:
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    var messageOutputIndex = MessageOutputIndex(state);
                    if (!textStarted)
                    {
                        textStarted = true;
                        await WriteEventAsync(response, "response.output_item.added", new JsonObject
                        {
                            ["output_index"] = messageOutputIndex,
                            ["item"] = MessageItem(state, "in_progress", string.Empty)
                        }, sequence++, cancellationToken);
                        await WriteEventAsync(response, "response.content_part.added", new JsonObject
                        {
                            ["item_id"] = MessageId(state),
                            ["output_index"] = messageOutputIndex,
                            ["content_index"] = 0,
                            ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = string.Empty, ["annotations"] = new JsonArray() }
                        }, sequence++, cancellationToken);
                    }
                    state.Text.Append(text.Text);
                    await WriteEventAsync(response, "response.output_text.delta", new JsonObject
                    {
                        ["item_id"] = MessageId(state),
                        ["output_index"] = messageOutputIndex,
                        ["content_index"] = 0,
                        ["delta"] = text.Text,
                        ["logprobs"] = new JsonArray()
                    }, sequence++, cancellationToken);
                    break;
                case GatewayReasoningDelta reasoning:
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    var reasoningOutputIndex = ReasoningOutputIndex(state);
                    if (!reasoningStarted)
                    {
                        reasoningStarted = true;
                        await WriteEventAsync(response, "response.output_item.added", new JsonObject
                        {
                            ["output_index"] = reasoningOutputIndex,
                            ["item"] = ReasoningItem(state, "in_progress")
                        }, sequence++, cancellationToken);
                    }
                    state.Reasoning.Append(reasoning.Text);
                    await WriteEventAsync(response, "response.reasoning_summary_text.delta", new JsonObject
                    {
                        ["item_id"] = ReasoningId(state),
                        ["output_index"] = reasoningOutputIndex,
                        ["summary_index"] = 0,
                        ["delta"] = reasoning.Text
                    }, sequence++, cancellationToken);
                    break;
                case GatewayToolCallStarted toolStarted:
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    var tool = state.Tool(toolStarted.Index);
                    tool.Id = string.IsNullOrEmpty(toolStarted.Id) ? $"call_{Guid.NewGuid():N}" : toolStarted.Id;
                    tool.Name = toolStarted.Name;
                    if (!tool.StartEmitted)
                    {
                        tool.StartEmitted = true;
                        await WriteEventAsync(response, "response.output_item.added", new JsonObject
                        {
                            ["output_index"] = ToolOutputIndex(state, toolStarted.Index),
                            ["item"] = FunctionItem(tool, "in_progress")
                        }, sequence++, cancellationToken);
                    }
                    break;
                case GatewayToolArgumentsDelta arguments:
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    var argumentTool = state.Tool(arguments.Index);
                    if (!argumentTool.StartEmitted)
                    {
                        argumentTool.StartEmitted = true;
                        if (string.IsNullOrEmpty(argumentTool.Id)) argumentTool.Id = $"call_{Guid.NewGuid():N}";
                        await WriteEventAsync(response, "response.output_item.added", new JsonObject
                        {
                            ["output_index"] = ToolOutputIndex(state, arguments.Index),
                            ["item"] = FunctionItem(argumentTool, "in_progress")
                        }, sequence++, cancellationToken);
                    }
                    argumentTool.Arguments.Append(arguments.JsonFragment);
                    await WriteEventAsync(response, "response.function_call_arguments.delta", new JsonObject
                    {
                        ["item_id"] = FunctionItemId(argumentTool),
                        ["output_index"] = ToolOutputIndex(state, arguments.Index),
                        ["delta"] = arguments.JsonFragment
                    }, sequence++, cancellationToken);
                    break;
                case GatewayToolCallCompleted toolCompleted:
                    if (completedTools.Add(toolCompleted.Index))
                    {
                        var completedTool = state.Tool(toolCompleted.Index);
                        await WriteEventAsync(response, "response.function_call_arguments.done", new JsonObject
                        {
                            ["item_id"] = FunctionItemId(completedTool),
                            ["output_index"] = ToolOutputIndex(state, toolCompleted.Index),
                            ["arguments"] = completedTool.Arguments.ToString()
                        }, sequence++, cancellationToken);
                        await WriteEventAsync(response, "response.output_item.done", new JsonObject
                        {
                            ["output_index"] = ToolOutputIndex(state, toolCompleted.Index),
                            ["item"] = FunctionItem(completedTool, "completed")
                        }, sequence++, cancellationToken);
                    }
                    break;
                case GatewayUsageUpdated usage:
                    state.PromptTokens = usage.PromptTokens;
                    state.CompletionTokens = usage.CompletionTokens;
                    break;
                case GatewayStreamError error:
                    await WriteEventAsync(response, "error", new JsonObject
                    {
                        ["code"] = error.Code,
                        ["message"] = error.Message
                    }, sequence++, cancellationToken);
                    break;
                case GatewayResponseCompleted completed:
                    if (!created)
                    {
                        created = true;
                        await WriteCreatedAsync(response, state, virtualModel.Name, sequence++, cancellationToken);
                    }
                    state.FinishReason = completed.FinishReason;
                    if (textStarted)
                    {
                        await WriteEventAsync(response, "response.output_text.done", new JsonObject
                        {
                            ["item_id"] = MessageId(state),
                            ["output_index"] = MessageOutputIndex(state),
                            ["content_index"] = 0,
                            ["text"] = state.Text.ToString(),
                            ["logprobs"] = new JsonArray()
                        }, sequence++, cancellationToken);
                        await WriteEventAsync(response, "response.output_item.done", new JsonObject
                        {
                            ["output_index"] = MessageOutputIndex(state),
                            ["item"] = MessageItem(state, "completed", state.Text.ToString())
                        }, sequence++, cancellationToken);
                    }
                    await WriteEventAsync(response, "response.completed", new JsonObject
                    {
                        ["response"] = BuildResponse(state, virtualModel.Name)
                    }, sequence++, cancellationToken);
                    break;
            }
        }

    }

    private static async Task<ChatResponseState> AccumulateAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        CancellationToken cancellationToken)
    {
        var state = new ChatResponseState { ResponseId = $"resp_{Guid.NewGuid():N}" };
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayResponseStarted started:
                    state.ResponseId = NormalizeResponseId(started.ResponseId);
                    state.CreatedAt = started.CreatedAtUnixSeconds;
                    break;
                case GatewayTextDelta text:
                    MessageOutputIndex(state);
                    state.Text.Append(text.Text);
                    break;
                case GatewayReasoningDelta reasoning:
                    ReasoningOutputIndex(state);
                    state.Reasoning.Append(reasoning.Text);
                    break;
                case GatewayToolCallStarted started:
                    ToolOutputIndex(state, started.Index);
                    state.Tool(started.Index).Id = string.IsNullOrEmpty(started.Id) ? $"call_{Guid.NewGuid():N}" : started.Id;
                    state.Tool(started.Index).Name = started.Name;
                    break;
                case GatewayToolArgumentsDelta delta:
                    ToolOutputIndex(state, delta.Index);
                    state.Tool(delta.Index).Arguments.Append(delta.JsonFragment);
                    break;
                case GatewayUsageUpdated usage:
                    state.PromptTokens = usage.PromptTokens;
                    state.CompletionTokens = usage.CompletionTokens;
                    break;
                case GatewayResponseCompleted completed: state.FinishReason = completed.FinishReason; break;
            }
        }
        return state;
    }

    private static JsonObject BuildResponse(
        ChatResponseState state,
        string model,
        string status = "completed",
        bool includeOutput = true)
    {
        var output = new JsonArray();
        if (includeOutput)
        {
            var indexedOutput = new List<(int Index, JsonObject Item)>();
            if (state.Reasoning.Length > 0 || state.ReasoningOutputIndex.HasValue)
                indexedOutput.Add((ReasoningOutputIndex(state), ReasoningItem(state, status)));
            if (state.Text.Length > 0 || state.MessageOutputIndex.HasValue || state.Tools.Count == 0)
                indexedOutput.Add((MessageOutputIndex(state), MessageItem(state, status, state.Text.ToString())));
            foreach (var (toolIndex, tool) in state.Tools)
                indexedOutput.Add((ToolOutputIndex(state, toolIndex), FunctionItem(tool, status)));
            foreach (var (_, item) in indexedOutput.OrderBy(item => item.Index)) output.Add(item);
        }
        return new JsonObject
        {
            ["id"] = state.ResponseId,
            ["object"] = "response",
            ["created_at"] = state.CreatedAt,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["parallel_tool_calls"] = true,
            ["error"] = state.FinishReason == GatewayFinishReason.Error
                ? new JsonObject { ["code"] = "upstream_error", ["message"] = "The upstream response failed." }
                : null,
            ["incomplete_details"] = state.FinishReason == GatewayFinishReason.Length
                ? new JsonObject { ["reason"] = "max_output_tokens" }
                : null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = state.PromptTokens,
                ["output_tokens"] = state.CompletionTokens,
                ["total_tokens"] = state.PromptTokens + state.CompletionTokens
            }
        };
    }

    private static JsonObject MessageItem(ChatResponseState state, string status, string text) => new()
    {
        ["id"] = MessageId(state),
        ["type"] = "message",
        ["status"] = status,
        ["role"] = "assistant",
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = text,
                ["annotations"] = new JsonArray()
            }
        }
    };

    private static JsonObject ReasoningItem(ChatResponseState state, string status) => new()
    {
        ["id"] = ReasoningId(state),
        ["type"] = "reasoning",
        ["status"] = status,
        ["summary"] = new JsonArray
        {
            new JsonObject { ["type"] = "summary_text", ["text"] = state.Reasoning.ToString() }
        }
    };

    private static JsonObject FunctionItem(ToolCallState tool, string status) => new()
    {
        ["id"] = FunctionItemId(tool),
        ["type"] = "function_call",
        ["status"] = status,
        ["call_id"] = string.IsNullOrEmpty(tool.Id) ? $"call_{Guid.NewGuid():N}" : tool.Id,
        ["name"] = tool.Name,
        ["arguments"] = tool.Arguments.ToString()
    };

    private static async Task WriteEventAsync(
        HttpResponse response,
        string type,
        JsonObject fields,
        int sequence,
        CancellationToken cancellationToken)
    {
        fields["type"] = type;
        fields["sequence_number"] = sequence;
        await response.WriteAsync($"event: {type}\ndata: {fields.ToJsonString()}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }

    private static Task WriteCreatedAsync(
        HttpResponse response,
        ChatResponseState state,
        string model,
        int sequence,
        CancellationToken cancellationToken) => WriteEventAsync(response, "response.created", new JsonObject
        {
            ["response"] = BuildResponse(state, model, "in_progress", includeOutput: false)
        }, sequence, cancellationToken);

    private static string NormalizeResponseId(string id) => id.StartsWith("resp_", StringComparison.Ordinal)
        ? id
        : $"resp_{Guid.NewGuid():N}";
    private static string MessageId(ChatResponseState state) => $"msg_{StableSuffix(state.ResponseId)}";
    private static string ReasoningId(ChatResponseState state) => $"rs_{StableSuffix(state.ResponseId)}";
    private static string FunctionItemId(ToolCallState tool) => $"fc_{StableSuffix(tool.Id)}";
    private static int MessageOutputIndex(ChatResponseState state) =>
        state.MessageOutputIndex ??= state.NextOutputIndex++;
    private static int ReasoningOutputIndex(ChatResponseState state) =>
        state.ReasoningOutputIndex ??= state.NextOutputIndex++;
    private static int ToolOutputIndex(ChatResponseState state, int toolIndex)
    {
        var tool = state.Tool(toolIndex);
        return tool.OutputIndex ??= state.NextOutputIndex++;
    }
    private static string StableSuffix(string value)
    {
        var sanitized = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (sanitized.Length >= 24) return sanitized[^24..];
        return sanitized.PadRight(24, '0');
    }
}
