using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Framing;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

/// <summary>
/// Same-dialect fast path for Responses. It preserves unknown items and events,
/// changing gateway-owned identity fields. Reasoning compatibility requires an
/// explicit client header; ordinary same-dialect requests remain pass-through.
/// </summary>
public sealed class OpenAiResponsesWireResponseWriter(GatewayRequestTracker tracker, RequestLogContext logContext) : IChatWireResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.OpenAiResponses;
    public BackendProtocol Protocol => BackendProtocol.OpenAiResponses;

    public async Task WriteAsync(
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        bool streaming,
        HttpContext httpContext)
    {
        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
        var summaryRequested = httpContext.Request.Headers["X-OllamaGateway-Responses-Compatibility"] == "reasoning-summary" &&
                               await SummaryRequestedAsync(upstreamResponse, httpContext.RequestAborted);
        if (streaming)
            await WriteStreamingAsync(stream, virtualModel, httpContext, summaryRequested);
        else
            await WriteBufferedAsync(stream, virtualModel, httpContext, summaryRequested);
    }

    private async Task WriteBufferedAsync(Stream stream, VirtualModel virtualModel, HttpContext context, bool summaryRequested)
    {
        var root = await JsonNode.ParseAsync(stream, cancellationToken: context.RequestAborted);
        if (root == null) return;
        SetVirtualModel(root, virtualModel.Name);
        if (summaryRequested) AddMissingSummaries(root);
        ObserveResponse(root);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(root.ToJsonString(), context.RequestAborted);
    }

    private async Task WriteStreamingAsync(Stream stream, VirtualModel virtualModel, HttpContext context, bool summaryRequested)
    {
        context.Response.ContentType = "text/event-stream";
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        await foreach (var frame in SseFrameReader.ReadAsync(stream, context.RequestAborted))
        {
            var data = frame.Data;
            var eventType = frame.EventType;
            try
            {
                var root = frame.HasData ? JsonNode.Parse(data) : null;
                if (root != null)
                {
                    SetVirtualModel(root, virtualModel.Name);
                    if (summaryRequested)
                    {
                        NormalizeReasoningEvent(root);
                        AddMissingSummaries(root);
                        if (eventType is "response.reasoning_part.added" or "response.reasoning_part.done" or
                            "response.reasoning_text.delta" or "response.reasoning_text.done")
                            eventType = ChatRequestDecoding.StringValue(root["type"]);
                    }
                    ObserveEvent(root, answer, reasoning);
                    data = root.ToJsonString();
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Unknown non-JSON events are forwarded unchanged.
            }

            foreach (var comment in frame.Comments)
                await context.Response.WriteAsync($": {comment}\n", context.RequestAborted);
            if (eventType != null)
                await context.Response.WriteAsync($"event: {eventType}\n", context.RequestAborted);
            if (frame.Id != null)
                await context.Response.WriteAsync($"id: {frame.Id}\n", context.RequestAborted);
            if (frame.RetryMilliseconds.HasValue)
                await context.Response.WriteAsync($"retry: {frame.RetryMilliseconds.Value}\n", context.RequestAborted);
            if (frame.HasData)
            {
                foreach (var line in data.Split('\n'))
                    await context.Response.WriteAsync($"data: {line}\n", context.RequestAborted);
            }
            await context.Response.WriteAsync("\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        logContext.Log.Answer = answer.ToString();
        logContext.Log.Thinking = reasoning.ToString();
    }

    private static async Task<bool> SummaryRequestedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.RequestMessage?.Content == null) return false;
        var body = await response.RequestMessage.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var request = JsonNode.Parse(body);
            return ChatRequestDecoding.StringValue(request?["reasoning"]?["summary"]) is "auto" or "concise" or "detailed";
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static void NormalizeReasoningEvent(JsonNode root)
    {
        // Some local Responses servers only emit raw reasoning extensions even
        // when the client requests summaries. Expose the same text through the
        // standard summary events; retain original reasoning content in items.
        var type = ChatRequestDecoding.StringValue(root["type"]);
        var normalized = type switch
        {
            "response.reasoning_part.added" => "response.reasoning_summary_part.added",
            "response.reasoning_part.done" => "response.reasoning_summary_part.done",
            "response.reasoning_text.delta" => "response.reasoning_summary_text.delta",
            "response.reasoning_text.done" => "response.reasoning_summary_text.done",
            _ => null
        };
        if (normalized == null) return;
        root["type"] = normalized;
        root["summary_index"] = root["content_index"]?.DeepClone() ?? JsonValue.Create(0);
        if (root["part"] is JsonObject part && ChatRequestDecoding.StringValue(part["type"]) == "reasoning_text")
            part["type"] = "summary_text";
    }

    private static void AddMissingSummaries(JsonNode root)
    {
        if (root["item"] is JsonObject item) AddMissingSummary(item);
        var response = root["response"] ?? root;
        if (response["output"] is JsonArray output)
            foreach (var entry in output.OfType<JsonObject>()) AddMissingSummary(entry);
    }

    private static void AddMissingSummary(JsonObject item)
    {
        if (ChatRequestDecoding.StringValue(item["type"]) != "reasoning" ||
            item["summary"] is JsonArray { Count: > 0 } || item["content"] is not JsonArray content)
            return;
        var summary = new JsonArray();
        foreach (var part in content.OfType<JsonObject>())
            if (ChatRequestDecoding.StringValue(part["type"]) == "reasoning_text")
                summary.Add(new JsonObject { ["type"] = "summary_text", ["text"] = part["text"]?.DeepClone() });
        if (summary.Count > 0) item["summary"] = summary;
    }

    private void ObserveResponse(JsonNode root)
    {
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        if (root["output"] is JsonArray output)
        {
            foreach (var item in output)
            {
                var type = ChatRequestDecoding.StringValue(item?["type"]);
                if (type == "message" && item?["content"] is JsonArray content)
                {
                    foreach (var part in content)
                    {
                        if (ChatRequestDecoding.StringValue(part?["type"]) == "output_text")
                            answer.Append(ChatRequestDecoding.StringValue(part?["text"]));
                    }
                }
                else if (type == "reasoning" && item?["summary"] is JsonArray summary)
                {
                    foreach (var part in summary)
                        reasoning.Append(ChatRequestDecoding.StringValue(part?["text"]));
                }
            }
        }
        ObserveUsage(root["usage"]);
        logContext.Log.Answer = answer.ToString();
        tracker.ObserveAnswer(logContext.Log.Answer);
        logContext.Log.Thinking = reasoning.ToString();
    }

    private void ObserveEvent(JsonNode root, StringBuilder answer, StringBuilder reasoning)
    {
        var type = ChatRequestDecoding.StringValue(root["type"]);
        if (type == "response.output_text.delta")
        {
            var delta = ChatRequestDecoding.StringValue(root["delta"]);
            tracker.ObserveAnswer(delta);
            answer.Append(delta);
        }
        else if (type is "response.reasoning_summary_text.delta" or "response.reasoning_text.delta")
            reasoning.Append(ChatRequestDecoding.StringValue(root["delta"]));
        else if (type == "response.completed")
            ObserveUsage(root["response"]?["usage"]);
    }

    private void ObserveUsage(JsonNode? usage)
    {
        if (usage == null) return;
        var input = usage["input_tokens"]?.GetValue<long>() ?? 0;
        var output = usage["output_tokens"]?.GetValue<long>() ?? 0;
        logContext.Log.PromptTokens = (int)input;
        logContext.Log.CompletionTokens = (int)output;
        logContext.Log.TotalTokens = (int)(input + output);
    }

    private static void SetVirtualModel(JsonNode root, string model)
    {
        if (root is JsonObject rootObject && rootObject.ContainsKey("model")) rootObject["model"] = model;
        if (root["response"] is JsonObject response && response.ContainsKey("model")) response["model"] = model;
    }
}
