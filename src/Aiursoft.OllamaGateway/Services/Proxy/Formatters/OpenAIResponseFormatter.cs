using System.Text;
using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy.Formatters;

public class OpenAIResponseFormatter(RequestLogContext logContext) : IResponseFormatter, IScopedDependency
{
    public async Task FormatResponseAsync(HttpResponseMessage upstreamResponse, HttpContext context, VirtualModel virtualModel, bool isStream, ModelType type)
    {
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            var errContent = await upstreamResponse.Content.ReadAsStringAsync(context.RequestAborted);
            logContext.Log.Answer = errContent;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(errContent, context.RequestAborted);
            return;
        }

        if (type == ModelType.Embedding)
        {
            await FormatEmbeddingsAsync(upstreamResponse, context, virtualModel);
            return;
        }

        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
        var chatId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (isStream)
        {
            context.Response.ContentType = "text/event-stream";
            var answerBuilder = new StringBuilder();
            var thinkBuilder = new StringBuilder();
            bool isFirstChunk = true;
            bool seenToolCalls = false;
            using var reader = new StreamReader(responseStream);
            string? line;
            while ((line = await reader.ReadLineAsync(context.RequestAborted)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var ollamaChunk = JsonNode.Parse(line);
                    if (ollamaChunk == null) continue;

                    var content = ollamaChunk["message"]?["content"]?.ToString() ?? string.Empty;
                    var reasoning = ollamaChunk["message"]?["thinking"]?.ToString() ?? ollamaChunk["message"]?["think"]?.ToString() ?? string.Empty;
                    var toolCalls = ollamaChunk["message"]?["tool_calls"]?.AsArray();
                    var isDone = ollamaChunk["done"]?.GetValue<bool>() ?? false;

                    if (!string.IsNullOrEmpty(content)) answerBuilder.Append(content);
                    if (!string.IsNullOrEmpty(reasoning)) thinkBuilder.Append(reasoning);
                    if (toolCalls?.Count > 0) seenToolCalls = true;

                    var openAiChunk = new JsonObject
                    {
                        ["id"] = chatId,
                        ["object"] = "chat.completion.chunk",
                        ["created"] = created,
                        ["model"] = virtualModel.Name,
                        ["choices"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = 0,
                                ["delta"] = new JsonObject(),
                                ["finish_reason"] = null
                            }
                        }
                    };

                    var delta = openAiChunk["choices"]![0]!["delta"]!.AsObject();
                    if (isFirstChunk) { delta["role"] = "assistant"; isFirstChunk = false; }
                    if (!string.IsNullOrEmpty(content)) delta["content"] = content;
                    if (!string.IsNullOrEmpty(reasoning)) delta["reasoning_content"] = reasoning;
                    if (toolCalls?.Count > 0)
                    {
                        var tcs = new JsonArray();
                        foreach (var tc in toolCalls)
                        {
                            if (tc == null) continue;
                            var tcObj = new JsonObject
                            {
                                ["index"] = 0,
                                ["id"] = "call-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                                ["type"] = "function"
                            };
                            var funcNode = tc["function"]?.AsObject();
                            if (funcNode != null)
                            {
                                var func = new JsonObject
                                {
                                    ["name"] = funcNode["name"]?.ToString(),
                                    ["arguments"] = funcNode["arguments"]?.ToJsonString()
                                };
                                tcObj["function"] = func;
                            }
                            tcs.Add(tcObj);
                        }
                        delta["tool_calls"] = tcs;
                    }

                    if (isDone)
                    {
                        openAiChunk["choices"]![0]!["finish_reason"] = seenToolCalls ? "tool_calls" : "stop";
                        logContext.Log.PromptTokens = (int)(ollamaChunk["prompt_eval_count"]?.GetValue<long>() ?? 0);
                        logContext.Log.CompletionTokens = (int)(ollamaChunk["eval_count"]?.GetValue<long>() ?? 0);
                        logContext.Log.TotalTokens = logContext.Log.PromptTokens + logContext.Log.CompletionTokens;
                    }

                    await context.Response.WriteAsync($"data: {openAiChunk.ToJsonString()}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                catch { /* Fallback */ }
            }
            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
            logContext.Log.Answer = answerBuilder.ToString();
            logContext.Log.Thinking = thinkBuilder.ToString();
        }
        else
        {
            var fullOllama = await upstreamResponse.Content.ReadAsStringAsync(context.RequestAborted);
            try
            {
                var ollamaRes = JsonNode.Parse(fullOllama);
                if (ollamaRes != null)
                {
                    var content = ollamaRes["message"]?["content"]?.ToString() ?? string.Empty;
                    var reasoning = ollamaRes["message"]?["thinking"]?.ToString() ?? ollamaRes["message"]?["think"]?.ToString() ?? string.Empty;
                    var toolCalls = ollamaRes["message"]?["tool_calls"]?.AsArray();
                    
                    var openAiRes = new JsonObject
                    {
                        ["id"] = chatId,
                        ["object"] = "chat.completion",
                        ["created"] = created,
                        ["model"] = virtualModel.Name,
                        ["choices"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["index"] = 0,
                                ["message"] = new JsonObject
                                {
                                    ["role"] = "assistant",
                                    ["content"] = content
                                },
                                ["finish_reason"] = toolCalls?.Count > 0 ? "tool_calls" : "stop"
                            }
                        },
                        ["usage"] = new JsonObject
                        {
                            ["prompt_tokens"] = (int)(ollamaRes["prompt_eval_count"]?.GetValue<long>() ?? 0),
                            ["completion_tokens"] = (int)(ollamaRes["eval_count"]?.GetValue<long>() ?? 0),
                            ["total_tokens"] = (int)((ollamaRes["prompt_eval_count"]?.GetValue<long>() ?? 0) + (ollamaRes["eval_count"]?.GetValue<long>() ?? 0))
                        }
                    };
                    if (!string.IsNullOrEmpty(reasoning)) openAiRes["choices"]![0]!["message"]!["reasoning_content"] = reasoning;
                    if (toolCalls?.Count > 0)
                    {
                        var tcs = new JsonArray();
                        foreach (var tc in toolCalls)
                        {
                            if (tc == null) continue;
                            var tcObj = new JsonObject
                            {
                                ["id"] = "call-" + Guid.NewGuid().ToString("N").Substring(0, 12),
                                ["type"] = "function"
                            };
                            var funcNode = tc["function"]?.AsObject();
                            if (funcNode != null)
                            {
                                var func = new JsonObject
                                {
                                    ["name"] = funcNode["name"]?.ToString(),
                                    ["arguments"] = funcNode["arguments"]?.ToJsonString()
                                };
                                tcObj["function"] = func;
                            }
                            tcs.Add(tcObj);
                        }
                        openAiRes["choices"]![0]!["message"]!["tool_calls"] = tcs;
                    }

                    logContext.Log.Answer = content;
                    logContext.Log.Thinking = reasoning;
                    logContext.Log.PromptTokens = openAiRes["usage"]!["prompt_tokens"]!.GetValue<int>();
                    logContext.Log.CompletionTokens = openAiRes["usage"]!["completion_tokens"]!.GetValue<int>();
                    logContext.Log.TotalTokens = openAiRes["usage"]!["total_tokens"]!.GetValue<int>();

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(openAiRes.ToJsonString(), context.RequestAborted);
                }
            }
            catch { /* Fallback */ }
        }
    }

    private async Task FormatEmbeddingsAsync(HttpResponseMessage upstreamResponse, HttpContext context, VirtualModel virtualModel)
    {
        var resultNode = await JsonNode.ParseAsync(await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted), cancellationToken: context.RequestAborted);
        if (resultNode != null)
        {
            var embeddings = resultNode["embeddings"]?.AsArray();
            var data = new JsonArray();
            if (embeddings != null)
            {
                for (int j = 0; j < embeddings.Count; j++)
                {
                    data.Add(new JsonObject
                    {
                        ["object"] = "embedding",
                        ["embedding"] = embeddings[j]!.DeepClone(),
                        ["index"] = j
                    });
                }
            }
            var openAiRes = new JsonObject
            {
                ["object"] = "list",
                ["data"] = data,
                ["model"] = virtualModel.Name,
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 0,
                    ["total_tokens"] = 0
                }
            };
            await context.Response.WriteAsync(openAiRes.ToJsonString(), context.RequestAborted);
        }
    }
}
