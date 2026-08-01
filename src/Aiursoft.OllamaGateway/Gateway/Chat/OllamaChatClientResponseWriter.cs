using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OllamaChatClientResponseWriter : IChatClientResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

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
        response.ContentType = "application/x-ndjson";
        var state = new ChatResponseState();
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayTextDelta text:
                    state.Text.Append(text.Text);
                    await WriteLineAsync(response, Chunk(virtualModel.Name, text.Text), cancellationToken);
                    break;
                case GatewayReasoningDelta reasoning:
                    state.Reasoning.Append(reasoning.Text);
                    var reasoningChunk = Chunk(virtualModel.Name, string.Empty);
                    reasoningChunk["message"]!["thinking"] = reasoning.Text;
                    await WriteLineAsync(response, reasoningChunk, cancellationToken);
                    break;
                case GatewayToolCallStarted started:
                    state.Tool(started.Index).Id = started.Id;
                    state.Tool(started.Index).Name = started.Name;
                    break;
                case GatewayToolArgumentsDelta arguments:
                    state.Tool(arguments.Index).Arguments.Append(arguments.JsonFragment);
                    break;
                case GatewayUsageUpdated usage:
                    state.PromptTokens = usage.PromptTokens;
                    state.CompletionTokens = usage.CompletionTokens;
                    break;
                case GatewayResponseCompleted completed:
                    state.FinishReason = completed.FinishReason;
                    await WriteLineAsync(response, Completed(virtualModel.Name, state), cancellationToken);
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

        response.ContentType = "application/json";
        await response.WriteAsync(Completed(virtualModel.Name, state).ToJsonString(), cancellationToken);
    }

    private static JsonObject Chunk(string model, string content)
    {
        return new JsonObject
        {
            ["model"] = model,
            ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
            ["done"] = false
        };
    }

    private static JsonObject Completed(string model, ChatResponseState state)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = state.Text.ToString() };
        if (state.Reasoning.Length > 0) message["thinking"] = state.Reasoning.ToString();
        if (state.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var (_, tool) in state.Tools)
            {
                JsonNode? arguments;
                try { arguments = JsonNode.Parse(tool.Arguments.ToString()); }
                catch (System.Text.Json.JsonException) { arguments = new JsonObject(); }
                tools.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["arguments"] = arguments ?? new JsonObject()
                    }
                });
            }
            message["tool_calls"] = tools;
        }

        return new JsonObject
        {
            ["model"] = model,
            ["message"] = message,
            ["done"] = true,
            ["done_reason"] = state.FinishReason == GatewayFinishReason.Length ? "length" : "stop",
            ["prompt_eval_count"] = state.PromptTokens,
            ["eval_count"] = state.CompletionTokens
        };
    }

    private static async Task WriteLineAsync(
        HttpResponse response,
        JsonObject line,
        CancellationToken cancellationToken)
    {
        await response.WriteAsync(line.ToJsonString() + "\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
