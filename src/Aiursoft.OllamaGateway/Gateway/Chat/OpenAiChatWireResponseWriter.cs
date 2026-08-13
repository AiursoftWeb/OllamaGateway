using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OpenAiChatWireResponseWriter(
    RequestLogContext logContext,
    ILogger<OpenAiChatWireResponseWriter> logger) : IChatWireResponseWriter
{
    private static readonly Regex ModelFieldRegex = new(
        @"""model""\s*:\s*""[^""]*""",
        RegexOptions.CultureInvariant);

    public ProtocolDialect Dialect => ProtocolDialect.OpenAiChatCompletions;
    public BackendProtocol Protocol => BackendProtocol.OpenAiChatCompletions;

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

    private async Task WriteStreamingAsync(
        Stream stream,
        VirtualModel virtualModel,
        HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.ContentType = "text/event-stream";
        var answer = new StringBuilder();
        var thinking = new StringBuilder();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(httpContext.RequestAborted)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.StartsWith("data:") && line != "data: [DONE]")
            {
                var jsonData = line["data:".Length..];
                if (jsonData.Length > 0 && jsonData[0] == ' ')
                    jsonData = jsonData[1..];

                ObserveChunk(jsonData, line, virtualModel.Name, answer, thinking);
                var modifiedData = ReplaceModelField(jsonData, virtualModel.Name);
                await response.WriteAsync($"data: {modifiedData}\n\n", httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }
            else if (line == "data: [DONE]")
            {
                await response.WriteAsync("data: [DONE]\n\n", httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }
            else
            {
                await response.WriteAsync(line + "\n", httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }
        }

        logContext.Log.Answer = answer.ToString();
        logContext.Log.Thinking = thinking.ToString();
    }

    private void ObserveChunk(
        string jsonData,
        string rawLine,
        string model,
        StringBuilder answer,
        StringBuilder thinking)
    {
        try
        {
            var chunk = JsonNode.Parse(jsonData);
            if (chunk == null) return;

            var deltaContent = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
            var deltaReasoning = chunk["choices"]?[0]?["delta"]?["reasoning_content"]?.ToString();
            var deltaToolCalls = chunk["choices"]?[0]?["delta"]?["tool_calls"];
            var finishReason = chunk["choices"]?[0]?["finish_reason"]?.ToString();

            if (!string.IsNullOrEmpty(deltaContent)) answer.Append(deltaContent);
            if (!string.IsNullOrEmpty(deltaReasoning)) thinking.Append(deltaReasoning);

            if (deltaToolCalls != null)
                logger.LogInformation("[SSE DEBUG] Upstream tool_call chunk (model={Model}): {RawLine}", model, rawLine);
            if (!string.IsNullOrEmpty(deltaContent) &&
                (deltaContent.Contains("<tool_call") ||
                 deltaContent.Contains("minimax:tool_call") ||
                 deltaContent.Contains("<function_call") ||
                 deltaContent.Contains("</tool_call>")))
            {
                logger.LogWarning(
                    "[SSE DEBUG] Possible parser failure: tool call markers in content (model={Model}): {RawLine}",
                    model,
                    rawLine);
            }
            if (finishReason == "tool_calls")
                logger.LogInformation("[SSE DEBUG] Upstream finish_reason=tool_calls (model={Model}): {RawLine}", model, rawLine);

            if (chunk["usage"] != null)
            {
                var promptTokens = chunk["usage"]!["prompt_tokens"]?.GetValue<long>() ?? 0;
                var completionTokens = chunk["usage"]!["completion_tokens"]?.GetValue<long>() ?? 0;
                logContext.Log.PromptTokens = (int)promptTokens;
                logContext.Log.CompletionTokens = (int)completionTokens;
                logContext.Log.TotalTokens = (int)(promptTokens + completionTokens);
            }
        }
        catch
        {
            // Logging is best-effort. The unmodified upstream event is still forwarded.
        }
    }

    private async Task WriteBufferedAsync(
        Stream stream,
        VirtualModel virtualModel,
        HttpContext httpContext)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, httpContext.RequestAborted);
        buffer.Seek(0, SeekOrigin.Begin);
        try
        {
            var root = await JsonNode.ParseAsync(buffer, cancellationToken: httpContext.RequestAborted);
            if (root == null) return;

            root["model"] = virtualModel.Name;
            var content = root["choices"]?[0]?["message"]?["content"]?.ToString() ?? string.Empty;
            var reasoning = root["choices"]?[0]?["message"]?["reasoning_content"]?.ToString() ?? string.Empty;
            var promptTokens = root["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0;
            var completionTokens = root["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0;
            logContext.Log.Answer = content;
            logContext.Log.Thinking = reasoning;
            logContext.Log.PromptTokens = (int)promptTokens;
            logContext.Log.CompletionTokens = (int)completionTokens;
            logContext.Log.TotalTokens = (int)(promptTokens + completionTokens);
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync(root.ToJsonString(), httpContext.RequestAborted);
        }
        catch
        {
            buffer.Seek(0, SeekOrigin.Begin);
            var rawResponse = await new StreamReader(buffer).ReadToEndAsync(httpContext.RequestAborted);
            logContext.Log.Answer = rawResponse;
            httpContext.Response.ContentType = "application/json";
            await httpContext.Response.WriteAsync(rawResponse, httpContext.RequestAborted);
        }
    }

    private static string ReplaceModelField(string json, string newModelName)
    {
        var escaped = newModelName.Replace("\\", "\\\\").Replace("$", "$$");
        return ModelFieldRegex.Replace(json, $"\"model\":\"{escaped}\"", 1);
    }
}
