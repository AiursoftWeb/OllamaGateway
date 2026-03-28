using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy.Formatters;

public class OllamaResponseFormatter(RequestLogContext logContext) : IResponseFormatter, IScopedDependency
{
    public async Task FormatResponseAsync(HttpResponseMessage upstreamResponse, HttpContext context, VirtualModel virtualModel, bool isStream, ModelType type)
    {
        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);

        if (isStream && upstreamResponse.IsSuccessStatusCode && type != ModelType.Embedding)
        {
            var answerBuilder = new StringBuilder();
            var thinkBuilder = new StringBuilder();
            using var reader = new StreamReader(responseStream);
            string? line;
            while ((line = await reader.ReadLineAsync(context.RequestAborted)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var chunkNode = JsonNode.Parse(line);
                    if (chunkNode != null)
                    {
                        chunkNode["model"] = virtualModel.Name;
                        var modifiedLine = chunkNode.ToJsonString();
                        await context.Response.WriteAsync(modifiedLine + "\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);

                        string? contentStr = type == ModelType.Chat 
                            ? chunkNode["message"]?["content"]?.ToString()
                            : chunkNode["response"]?.ToString();
                            
                        if (!string.IsNullOrEmpty(contentStr))
                        {
                            answerBuilder.Append(contentStr);
                        }

                        if (type == ModelType.Chat)
                        {
                            string? thinkStr = chunkNode["message"]?["thinking"]?.ToString() 
                                           ?? chunkNode["message"]?["think"]?.ToString();
                            if (!string.IsNullOrEmpty(thinkStr))
                            {
                                thinkBuilder.Append(thinkStr);
                            }
                        }

                        bool isDone = chunkNode["done"]?.GetValue<bool>() == true;
                        if (isDone)
                        {
                            logContext.Log.PromptTokens = (int)(chunkNode["prompt_eval_count"]?.GetValue<long>() ?? 0);
                            logContext.Log.CompletionTokens = (int)(chunkNode["eval_count"]?.GetValue<long>() ?? 0);
                            logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
                        }
                        continue;
                    }
                }
                catch { /* Fallback */ }
                
                await context.Response.WriteAsync(line + "\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }

            logContext.Log.Answer = answerBuilder.ToString();
            logContext.Log.Thinking = thinkBuilder.ToString();
        }
        else
        {
            var fullResponse = await upstreamResponse.Content.ReadAsStringAsync(context.RequestAborted);
            try
            {
                var responseNode = JsonNode.Parse(fullResponse);
                if (responseNode != null)
                {
                    responseNode["model"] = virtualModel.Name;
                    fullResponse = responseNode.ToJsonString();

                    if (type != ModelType.Embedding)
                    {
                        string? contentStr = type == ModelType.Chat 
                            ? responseNode["message"]?["content"]?.ToString()
                            : responseNode["response"]?.ToString();
                        logContext.Log.Answer = contentStr ?? string.Empty;

                        if (type == ModelType.Chat)
                        {
                            string? thinkStr = responseNode["message"]?["thinking"]?.ToString() 
                                           ?? responseNode["message"]?["think"]?.ToString();
                            logContext.Log.Thinking = thinkStr ?? string.Empty;
                        }

                        logContext.Log.PromptTokens = (int)(responseNode["prompt_eval_count"]?.GetValue<long>() ?? 0);
                        logContext.Log.CompletionTokens = (int)(responseNode["eval_count"]?.GetValue<long>() ?? 0);
                        logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
                    }
                }
            }
            catch { /* Fallback */ }
            
            await context.Response.WriteAsync(fullResponse, context.RequestAborted);
        }
    }
}
