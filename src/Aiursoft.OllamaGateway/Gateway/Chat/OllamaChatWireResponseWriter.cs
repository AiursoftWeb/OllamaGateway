using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class OllamaChatWireResponseWriter(RequestLogContext logContext) : IChatWireResponseWriter
{
    public ProtocolDialect Dialect => ProtocolDialect.OllamaNative;
    public BackendProtocol Protocol => BackendProtocol.OllamaNative;

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
        var answer = new StringBuilder();
        var thinking = new StringBuilder();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(httpContext.RequestAborted)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                var chunk = JsonNode.Parse(line);
                if (chunk != null)
                {
                    chunk["model"] = virtualModel.Name;
                    await httpContext.Response.WriteAsync(
                        chunk.ToJsonString() + "\n",
                        httpContext.RequestAborted);
                    await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
                    ObserveChunk(chunk, answer, thinking);
                    continue;
                }
            }
            catch
            {
                // Preserve malformed provider records as-is on the wire fast path.
            }

            await httpContext.Response.WriteAsync(line + "\n", httpContext.RequestAborted);
            await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted);
        }

        logContext.Log.Answer = answer.ToString();
        logContext.Log.Thinking = thinking.ToString();
    }

    private void ObserveChunk(JsonNode chunk, StringBuilder answer, StringBuilder thinking)
    {
        var content = chunk["message"]?["content"]?.ToString();
        if (!string.IsNullOrEmpty(content)) answer.Append(content);
        var reasoning = chunk["message"]?["thinking"]?.ToString()
                        ?? chunk["message"]?["think"]?.ToString();
        if (!string.IsNullOrEmpty(reasoning)) thinking.Append(reasoning);

        if (chunk["done"]?.GetValue<bool>() != true) return;
        logContext.Log.PromptTokens = (int)(chunk["prompt_eval_count"]?.GetValue<long>() ?? 0);
        logContext.Log.CompletionTokens = (int)(chunk["eval_count"]?.GetValue<long>() ?? 0);
        logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
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
            if (root != null)
            {
                root["model"] = virtualModel.Name;
                logContext.Log.Answer = root["message"]?["content"]?.ToString() ?? string.Empty;
                logContext.Log.Thinking = root["message"]?["thinking"]?.ToString()
                                          ?? root["message"]?["think"]?.ToString()
                                          ?? string.Empty;
                logContext.Log.PromptTokens = (int)(root["prompt_eval_count"]?.GetValue<long>() ?? 0);
                logContext.Log.CompletionTokens = (int)(root["eval_count"]?.GetValue<long>() ?? 0);
                logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
                await httpContext.Response.WriteAsync(root.ToJsonString(), httpContext.RequestAborted);
                return;
            }
        }
        catch
        {
            // Preserve an unrecognized same-protocol response as-is.
        }

        buffer.Seek(0, SeekOrigin.Begin);
        await buffer.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted);
    }
}
