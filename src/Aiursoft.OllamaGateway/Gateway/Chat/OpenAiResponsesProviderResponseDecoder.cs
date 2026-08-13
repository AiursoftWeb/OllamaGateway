using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Framing;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiResponsesProviderResponseDecoder : IChatProviderResponseDecoder
{
    public BackendProtocol Protocol => BackendProtocol.OpenAiResponses;

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
                foreach (var item in DecodeResponse(root)) yield return item;
            }
            yield break;
        }

        var started = false;
        var completed = false;
        var startedTools = new HashSet<int>();
        await foreach (var frame in SseFrameReader.ReadAsync(responseStream, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(frame.Data)) continue;
            JsonNode? data;
            try { data = JsonNode.Parse(frame.Data); }
            catch (System.Text.Json.JsonException) { data = null; }
            if (data == null)
            {
                yield return new GatewayOpaqueEvent(
                    ProtocolDialect.OpenAiResponses,
                    frame.EventType ?? "message",
                    frame.Data);
                continue;
            }

            var eventType = ChatRequestDecoding.StringValue(data["type"], frame.EventType ?? "message");
            switch (eventType)
            {
                case "response.created":
                case "response.in_progress":
                    if (!started)
                    {
                        started = true;
                        yield return ResponseStarted(data["response"] ?? data);
                    }
                    break;
                case "response.output_item.added":
                    if (!started)
                    {
                        started = true;
                        yield return ResponseStarted(data["response"] ?? data);
                    }
                    var added = data["item"];
                    if (ChatRequestDecoding.StringValue(added?["type"]) == "function_call")
                    {
                        var index = ChatRequestDecoding.IntValue(data["output_index"]) ?? 0;
                        if (startedTools.Add(index))
                        {
                            yield return new GatewayToolCallStarted(
                                index,
                                ChatRequestDecoding.StringValue(added?["call_id"] ?? added?["id"]),
                                ChatRequestDecoding.StringValue(added?["name"]));
                        }
                    }
                    break;
                case "response.output_text.delta":
                    yield return new GatewayTextDelta(ChatRequestDecoding.StringValue(data["delta"]));
                    break;
                case "response.refusal.delta":
                    yield return new GatewayTextDelta(ChatRequestDecoding.StringValue(data["delta"]));
                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    yield return new GatewayReasoningDelta(ChatRequestDecoding.StringValue(data["delta"]));
                    break;
                case "response.function_call_arguments.delta":
                    var argumentIndex = ChatRequestDecoding.IntValue(data["output_index"]) ?? 0;
                    if (startedTools.Add(argumentIndex))
                    {
                        yield return new GatewayToolCallStarted(
                            argumentIndex,
                            ChatRequestDecoding.StringValue(data["call_id"] ?? data["item_id"]),
                            ChatRequestDecoding.StringValue(data["name"]));
                    }
                    yield return new GatewayToolArgumentsDelta(
                        argumentIndex,
                        ChatRequestDecoding.StringValue(data["delta"]));
                    break;
                case "response.function_call_arguments.done":
                    var doneIndex = ChatRequestDecoding.IntValue(data["output_index"]) ?? 0;
                    if (startedTools.Add(doneIndex))
                    {
                        yield return new GatewayToolCallStarted(
                            doneIndex,
                            ChatRequestDecoding.StringValue(data["call_id"] ?? data["item_id"]),
                            ChatRequestDecoding.StringValue(data["name"]));
                        var arguments = ChatRequestDecoding.StringValue(data["arguments"]);
                        if (!string.IsNullOrEmpty(arguments))
                            yield return new GatewayToolArgumentsDelta(doneIndex, arguments);
                    }
                    yield return new GatewayToolCallCompleted(doneIndex);
                    break;
                case "response.completed":
                    var response = data["response"] ?? data;
                    if (!started)
                    {
                        started = true;
                        yield return ResponseStarted(response);
                    }
                    if (response["usage"] != null) yield return Usage(response["usage"]!);
                    foreach (var index in startedTools) yield return new GatewayToolCallCompleted(index);
                    yield return new GatewayResponseCompleted(MapFinishReason(response));
                    completed = true;
                    break;
                case "response.failed":
                case "response.incomplete":
                    var failed = data["response"] ?? data;
                    yield return new GatewayStreamError(
                        eventType,
                        ChatRequestDecoding.StringValue(
                            failed["error"]?["message"] ?? failed["incomplete_details"]?["reason"],
                            "The upstream response did not complete."));
                    yield return new GatewayResponseCompleted(GatewayFinishReason.Error);
                    completed = true;
                    break;
                case "error":
                    yield return new GatewayStreamError(
                        ChatRequestDecoding.StringValue(data["code"], "upstream_error"),
                        ChatRequestDecoding.StringValue(data["message"], "The upstream stream failed."));
                    break;
                default:
                    yield return new GatewayOpaqueEvent(ProtocolDialect.OpenAiResponses, eventType, frame.Data);
                    break;
            }
        }

        if (!completed) yield return new GatewayResponseCompleted(GatewayFinishReason.Stop);
    }

    private static IEnumerable<GatewayChatEvent> DecodeResponse(JsonNode root)
    {
        yield return ResponseStarted(root);
        if (root["output"] is JsonArray output)
        {
            for (var index = 0; index < output.Count; index++)
            {
                var item = output[index];
                switch (ChatRequestDecoding.StringValue(item?["type"]))
                {
                    case "message":
                        if (item?["content"] is not JsonArray content) break;
                        foreach (var part in content)
                        {
                            var type = ChatRequestDecoding.StringValue(part?["type"]);
                            if (type == "output_text")
                                yield return new GatewayTextDelta(ChatRequestDecoding.StringValue(part?["text"]));
                            else if (type == "refusal")
                                yield return new GatewayTextDelta(ChatRequestDecoding.StringValue(part?["refusal"]));
                        }
                        break;
                    case "reasoning":
                        if (item?["summary"] is JsonArray summary)
                        {
                            foreach (var part in summary)
                            {
                                var text = ChatRequestDecoding.StringValue(part?["text"]);
                                if (!string.IsNullOrEmpty(text)) yield return new GatewayReasoningDelta(text);
                            }
                        }
                        break;
                    case "function_call":
                        yield return new GatewayToolCallStarted(
                            index,
                            ChatRequestDecoding.StringValue(item?["call_id"] ?? item?["id"]),
                            ChatRequestDecoding.StringValue(item?["name"]));
                        yield return new GatewayToolArgumentsDelta(
                            index,
                            ChatRequestDecoding.StringValue(item?["arguments"], "{}"));
                        yield return new GatewayToolCallCompleted(index);
                        break;
                }
            }
        }

        if (root["usage"] != null) yield return Usage(root["usage"]!);
        yield return new GatewayResponseCompleted(MapFinishReason(root));
    }

    private static GatewayResponseStarted ResponseStarted(JsonNode root) => new(
        ChatRequestDecoding.StringValue(root["id"], $"resp_{Guid.NewGuid():N}"),
        ChatRequestDecoding.StringValue(root["model"]),
        root["created_at"]?.GetValue<long>() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static GatewayUsageUpdated Usage(JsonNode usage) => new(
        usage["input_tokens"]?.GetValue<long>() ?? 0,
        usage["output_tokens"]?.GetValue<long>() ?? 0);

    private static GatewayFinishReason MapFinishReason(JsonNode response)
    {
        var status = ChatRequestDecoding.StringValue(response["status"]);
        if (status == "failed" || response["error"] != null) return GatewayFinishReason.Error;
        var reason = ChatRequestDecoding.StringValue(response["incomplete_details"]?["reason"]);
        return reason is "max_output_tokens" or "max_tokens"
            ? GatewayFinishReason.Length
            : GatewayFinishReason.Stop;
    }
}
