using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatClientResponseWriter : IChatClientResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;

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
        var state = new ChatResponseState();
        var firstDelta = true;
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayResponseStarted started:
                    state.ResponseId = started.ResponseId;
                    state.CreatedAt = started.CreatedAtUnixSeconds;
                    break;
                case GatewayTextDelta text:
                    state.Text.Append(text.Text);
                    await WriteChunkAsync(response, state, virtualModel.Name,
                        Delta(firstDelta, content: text.Text), null, null, cancellationToken);
                    firstDelta = false;
                    break;
                case GatewayReasoningDelta reasoning:
                    state.Reasoning.Append(reasoning.Text);
                    await WriteChunkAsync(response, state, virtualModel.Name,
                        Delta(firstDelta, reasoning: reasoning.Text), null, null, cancellationToken);
                    firstDelta = false;
                    break;
                case GatewayToolCallStarted toolStarted:
                    var tool = state.Tool(toolStarted.Index);
                    tool.Id = string.IsNullOrEmpty(toolStarted.Id) ? $"call_{Guid.NewGuid():N}"[..13] : toolStarted.Id;
                    tool.Name = toolStarted.Name;
                    break;
                case GatewayToolArgumentsDelta arguments:
                    var argumentTool = state.Tool(arguments.Index);
                    argumentTool.Arguments.Append(arguments.JsonFragment);
                    var argumentDelta = Delta(firstDelta);
                    var function = new JsonObject { ["arguments"] = arguments.JsonFragment };
                    var toolCall = new JsonObject
                    {
                        ["index"] = arguments.Index,
                        ["function"] = function
                    };
                    if (!argumentTool.StartEmitted)
                    {
                        argumentTool.StartEmitted = true;
                        toolCall["id"] = argumentTool.Id;
                        toolCall["type"] = "function";
                        function["name"] = argumentTool.Name;
                    }
                    argumentDelta["tool_calls"] = new JsonArray
                    {
                        toolCall
                    };
                    await WriteChunkAsync(response, state, virtualModel.Name, argumentDelta, null, null, cancellationToken);
                    firstDelta = false;
                    break;
                case GatewayUsageUpdated usage:
                    state.PromptTokens = usage.PromptTokens;
                    state.CompletionTokens = usage.CompletionTokens;
                    break;
                case GatewayResponseCompleted completed:
                    state.FinishReason = completed.FinishReason;
                    await WriteChunkAsync(
                        response,
                        state,
                        virtualModel.Name,
                        Delta(firstDelta),
                        FinishReason(completed.FinishReason),
                        Usage(state),
                        cancellationToken);
                    await response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                    await response.Body.FlushAsync(cancellationToken);
                    firstDelta = false;
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
        var state = await AccumulateAsync(events, cancellationToken);
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = state.Text.ToString()
        };
        if (state.Reasoning.Length > 0) message["reasoning_content"] = state.Reasoning.ToString();
        if (state.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var (_, tool) in state.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["id"] = string.IsNullOrEmpty(tool.Id) ? $"call_{Guid.NewGuid():N}"[..13] : tool.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["arguments"] = tool.Arguments.ToString()
                    }
                });
            }
            message["tool_calls"] = tools;
        }

        var root = new JsonObject
        {
            ["id"] = state.ResponseId,
            ["object"] = "chat.completion",
            ["created"] = state.CreatedAt,
            ["model"] = virtualModel.Name,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = FinishReason(state.FinishReason)
                }
            },
            ["usage"] = Usage(state)
        };
        response.ContentType = "application/json";
        await response.WriteAsync(root.ToJsonString(), cancellationToken);
    }

    private static async Task<ChatResponseState> AccumulateAsync(
        IAsyncEnumerable<GatewayChatEvent> events,
        CancellationToken cancellationToken)
    {
        var state = new ChatResponseState();
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayResponseStarted started:
                    state.ResponseId = started.ResponseId;
                    state.CreatedAt = started.CreatedAtUnixSeconds;
                    break;
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
        return state;
    }

    private static JsonObject Delta(bool first, string? content = null, string? reasoning = null)
    {
        var delta = new JsonObject();
        if (first) delta["role"] = "assistant";
        if (content != null) delta["content"] = content;
        if (reasoning != null) delta["reasoning_content"] = reasoning;
        return delta;
    }

    private static JsonObject Usage(ChatResponseState state)
    {
        return new JsonObject
        {
            ["prompt_tokens"] = state.PromptTokens,
            ["completion_tokens"] = state.CompletionTokens,
            ["total_tokens"] = state.PromptTokens + state.CompletionTokens
        };
    }

    private static string FinishReason(GatewayFinishReason reason)
    {
        return reason switch
        {
            GatewayFinishReason.Length => "length",
            GatewayFinishReason.ToolCalls => "tool_calls",
            GatewayFinishReason.ContentFilter => "content_filter",
            _ => "stop"
        };
    }

    private static async Task WriteChunkAsync(
        HttpResponse response,
        ChatResponseState state,
        string model,
        JsonObject delta,
        string? finishReason,
        JsonObject? usage,
        CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["id"] = state.ResponseId,
            ["object"] = "chat.completion.chunk",
            ["created"] = state.CreatedAt,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason
                }
            }
        };
        if (usage != null && (state.PromptTokens > 0 || state.CompletionTokens > 0)) root["usage"] = usage;
        await response.WriteAsync($"data: {root.ToJsonString()}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
