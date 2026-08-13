using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Framing;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatProviderResponseDecoder : IChatProviderResponseDecoder
{
    public BackendProtocol Protocol => BackendProtocol.OpenAiChatCompletions;

    private const ProtocolDialect Dialect = ProtocolDialect.OpenAiChatCompletions;

    public async IAsyncEnumerable<GatewayChatEvent> DecodeAsync(
        Stream responseStream,
        bool streaming,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!streaming)
        {
            var root = await JsonNode.ParseAsync(responseStream, cancellationToken: cancellationToken);
            if (root == null) yield break;
            foreach (var item in DecodeObject(root, true)) yield return item;
            yield break;
        }

        var started = false;
        var completed = false;
        var startedTools = new HashSet<int>();
        await foreach (var frame in SseFrameReader.ReadAsync(responseStream, cancellationToken))
        {
            if (frame.Data == "[DONE]")
            {
                if (!completed) yield return new GatewayResponseCompleted(GatewayFinishReason.Stop);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(frame.Data)) continue;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(frame.Data);
            }
            catch (System.Text.Json.JsonException)
            {
                root = null;
            }
            if (root == null)
            {
                yield return new GatewayOpaqueEvent(Dialect, frame.EventType ?? "message", frame.Data);
                continue;
            }

            if (!started)
            {
                started = true;
                yield return ResponseStarted(root);
            }

            var choice = root["choices"]?[0];
            var delta = choice?["delta"];
            var reasoning = delta?["reasoning_content"]?.ToString();
            if (!string.IsNullOrEmpty(reasoning)) yield return new GatewayReasoningDelta(reasoning);
            var text = delta?["content"]?.ToString();
            if (!string.IsNullOrEmpty(text)) yield return new GatewayTextDelta(text);

            if (delta?["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var toolCall in toolCalls)
                {
                    var index = toolCall?["index"]?.GetValue<int>() ?? 0;
                    var id = toolCall?["id"]?.ToString() ?? string.Empty;
                    var name = toolCall?["function"]?["name"]?.ToString() ?? string.Empty;
                    if (startedTools.Add(index))
                        yield return new GatewayToolCallStarted(index, id, name);
                    var arguments = toolCall?["function"]?["arguments"]?.ToString();
                    if (!string.IsNullOrEmpty(arguments))
                        yield return new GatewayToolArgumentsDelta(index, arguments);
                }
            }

            if (root["usage"] != null)
                yield return Usage(root["usage"]!);

            var finish = choice?["finish_reason"]?.ToString();
            if (!string.IsNullOrEmpty(finish))
            {
                foreach (var toolIndex in startedTools) yield return new GatewayToolCallCompleted(toolIndex);
                yield return new GatewayResponseCompleted(MapFinishReason(finish));
                completed = true;
            }
        }

        if (!completed) yield return new GatewayResponseCompleted(GatewayFinishReason.Stop);
    }

    private static IEnumerable<GatewayChatEvent> DecodeObject(JsonNode root, bool completed)
    {
        yield return ResponseStarted(root);
        var choice = root["choices"]?[0];
        var message = choice?["message"];
        var reasoning = message?["reasoning_content"]?.ToString();
        if (!string.IsNullOrEmpty(reasoning)) yield return new GatewayReasoningDelta(reasoning);
        var text = message?["content"]?.ToString();
        if (!string.IsNullOrEmpty(text)) yield return new GatewayTextDelta(text);

        if (message?["tool_calls"] is JsonArray toolCalls)
        {
            for (var index = 0; index < toolCalls.Count; index++)
            {
                var toolCall = toolCalls[index];
                var actualIndex = toolCall?["index"]?.GetValue<int>() ?? index;
                yield return new GatewayToolCallStarted(
                    actualIndex,
                    toolCall?["id"]?.ToString() ?? string.Empty,
                    toolCall?["function"]?["name"]?.ToString() ?? string.Empty);
                var arguments = toolCall?["function"]?["arguments"]?.ToString() ?? "{}";
                yield return new GatewayToolArgumentsDelta(actualIndex, arguments);
                yield return new GatewayToolCallCompleted(actualIndex);
            }
        }

        if (root["usage"] != null) yield return Usage(root["usage"]!);
        if (completed)
            yield return new GatewayResponseCompleted(MapFinishReason(choice?["finish_reason"]?.ToString()));
    }

    private static GatewayResponseStarted ResponseStarted(JsonNode root)
    {
        return new GatewayResponseStarted(
            root["id"]?.ToString() ?? $"chatcmpl-{Guid.NewGuid():N}",
            root["model"]?.ToString() ?? string.Empty,
            root["created"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static GatewayUsageUpdated Usage(JsonNode usage)
    {
        return new GatewayUsageUpdated(
            usage["prompt_tokens"]?.GetValue<long>() ?? 0,
            usage["completion_tokens"]?.GetValue<long>() ?? 0);
    }

    private static GatewayFinishReason MapFinishReason(string? reason)
    {
        return reason switch
        {
            "stop" => GatewayFinishReason.Stop,
            "length" => GatewayFinishReason.Length,
            "tool_calls" or "function_call" => GatewayFinishReason.ToolCalls,
            "content_filter" => GatewayFinishReason.ContentFilter,
            null => GatewayFinishReason.Stop,
            _ => GatewayFinishReason.Unknown
        };
    }
}
