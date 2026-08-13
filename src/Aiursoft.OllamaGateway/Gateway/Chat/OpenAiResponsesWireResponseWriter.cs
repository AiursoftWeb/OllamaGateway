using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Gateway.Framing;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

/// <summary>
/// Same-dialect fast path for Responses. It preserves unknown items and events,
/// changing only gateway-owned identity fields while observing usage and text.
/// </summary>
public sealed class OpenAiResponsesWireResponseWriter(RequestLogContext logContext) : IChatWireResponseWriter
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
        if (streaming)
            await WriteStreamingAsync(stream, virtualModel, httpContext);
        else
            await WriteBufferedAsync(stream, virtualModel, httpContext);
    }

    private async Task WriteBufferedAsync(Stream stream, VirtualModel virtualModel, HttpContext context)
    {
        var root = await JsonNode.ParseAsync(stream, cancellationToken: context.RequestAborted);
        if (root == null) return;
        SetVirtualModel(root, virtualModel.Name);
        ObserveResponse(root);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(root.ToJsonString(), context.RequestAborted);
    }

    private async Task WriteStreamingAsync(Stream stream, VirtualModel virtualModel, HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        await foreach (var frame in SseFrameReader.ReadAsync(stream, context.RequestAborted))
        {
            var data = frame.Data;
            try
            {
                var root = JsonNode.Parse(data);
                if (root != null)
                {
                    SetVirtualModel(root, virtualModel.Name);
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
            if (frame.EventType != null)
                await context.Response.WriteAsync($"event: {frame.EventType}\n", context.RequestAborted);
            if (frame.Id != null)
                await context.Response.WriteAsync($"id: {frame.Id}\n", context.RequestAborted);
            if (frame.RetryMilliseconds.HasValue)
                await context.Response.WriteAsync($"retry: {frame.RetryMilliseconds.Value}\n", context.RequestAborted);
            foreach (var line in data.Split('\n'))
                await context.Response.WriteAsync($"data: {line}\n", context.RequestAborted);
            await context.Response.WriteAsync("\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }

        logContext.Log.Answer = answer.ToString();
        logContext.Log.Thinking = reasoning.ToString();
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
        logContext.Log.Thinking = reasoning.ToString();
    }

    private void ObserveEvent(JsonNode root, StringBuilder answer, StringBuilder reasoning)
    {
        var type = ChatRequestDecoding.StringValue(root["type"]);
        if (type == "response.output_text.delta")
            answer.Append(ChatRequestDecoding.StringValue(root["delta"]));
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
