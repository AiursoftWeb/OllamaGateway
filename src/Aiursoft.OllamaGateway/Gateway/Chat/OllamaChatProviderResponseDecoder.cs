using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Framing;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OllamaChatProviderResponseDecoder : IChatProviderResponseDecoder
{
    public ProviderType ProviderType => ProviderType.Ollama;

    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;

    public async IAsyncEnumerable<GatewayChatEvent> DecodeAsync(
        Stream responseStream,
        bool streaming,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!streaming)
        {
            var root = await JsonNode.ParseAsync(responseStream, cancellationToken: cancellationToken);
            if (root != null)
            {
                foreach (var item in DecodeObject(root)) yield return item;
            }
            yield break;
        }

        var started = false;
        var completed = false;
        var hasToolCalls = false;
        await foreach (var record in NdjsonRecordReader.ReadAsync(responseStream, cancellationToken))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(record);
            }
            catch (System.Text.Json.JsonException)
            {
                root = null;
            }
            if (root == null)
            {
                yield return new GatewayOpaqueEvent(Dialect, "record", record);
                continue;
            }

            if (!started)
            {
                started = true;
                yield return ResponseStarted(root);
            }

            if (root["message"]?["tool_calls"] is JsonArray { Count: > 0 })
                hasToolCalls = true;
            foreach (var item in DecodePayload(root)) yield return item;
            if (root["done"]?.GetValue<bool>() == true)
            {
                yield return Usage(root);
                yield return new GatewayResponseCompleted(MapFinishReason(
                    root["done_reason"]?.ToString(),
                    hasToolCalls));
                completed = true;
            }
        }

        if (!completed) yield return new GatewayResponseCompleted(GatewayFinishReason.Stop);
    }

    private static IEnumerable<GatewayChatEvent> DecodeObject(JsonNode root)
    {
        yield return ResponseStarted(root);
        foreach (var item in DecodePayload(root)) yield return item;
        yield return Usage(root);
        yield return new GatewayResponseCompleted(MapFinishReason(
            root["done_reason"]?.ToString(),
            root["message"]?["tool_calls"] is JsonArray { Count: > 0 }));
    }

    private static IEnumerable<GatewayChatEvent> DecodePayload(JsonNode root)
    {
        var message = root["message"];
        var reasoning = message?["thinking"]?.ToString() ?? message?["think"]?.ToString();
        if (!string.IsNullOrEmpty(reasoning)) yield return new GatewayReasoningDelta(reasoning);
        var text = message?["content"]?.ToString();
        if (!string.IsNullOrEmpty(text)) yield return new GatewayTextDelta(text);

        if (message?["tool_calls"] is not JsonArray toolCalls) yield break;
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            yield return new GatewayToolCallStarted(
                index,
                toolCall?["id"]?.ToString() ?? $"call_{Guid.NewGuid():N}"[..13],
                toolCall?["function"]?["name"]?.ToString() ?? string.Empty);
            yield return new GatewayToolArgumentsDelta(
                index,
                toolCall?["function"]?["arguments"]?.ToJsonString() ?? "{}");
            yield return new GatewayToolCallCompleted(index);
        }
    }

    private static GatewayResponseStarted ResponseStarted(JsonNode root)
    {
        return new GatewayResponseStarted(
            $"chatcmpl-{Guid.NewGuid():N}"[..21],
            root["model"]?.ToString() ?? string.Empty,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static GatewayUsageUpdated Usage(JsonNode root)
    {
        return new GatewayUsageUpdated(
            root["prompt_eval_count"]?.GetValue<long>() ?? 0,
            root["eval_count"]?.GetValue<long>() ?? 0);
    }

    private static GatewayFinishReason MapFinishReason(string? reason, bool hasToolCalls)
    {
        if (hasToolCalls) return GatewayFinishReason.ToolCalls;
        return reason switch
        {
            "length" => GatewayFinishReason.Length,
            "stop" or null => GatewayFinishReason.Stop,
            _ => GatewayFinishReason.Unknown
        };
    }
}
