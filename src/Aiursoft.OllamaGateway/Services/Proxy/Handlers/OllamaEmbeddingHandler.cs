using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services.Proxy.Formatters;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy.Handlers;

public interface IOllamaEmbeddingHandler : IScopedDependency
{
    Task ProxyEmbedAsync(JsonObject input, HttpContext context);
}

public class OllamaEmbeddingHandler(
    ILogger<OllamaEmbeddingHandler> logger,
    IModelSelectionService modelSelectionService,
    IUpstreamExecutor upstreamExecutor,
    ProxyTelemetryService telemetryService,
    OllamaResponseFormatter ollamaFormatter,
    OpenAIResponseFormatter openAiFormatter,
    GlobalSettingsService globalSettingsService) : IOllamaEmbeddingHandler
{
    public async Task ProxyEmbedAsync(JsonObject input, HttpContext context)
    {
        string? modelName = input["model"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            modelName = await globalSettingsService.GetDefaultEmbeddingModelAsync();
        }

        var (virtualModel, backend) = await modelSelectionService.SelectModelAsync(modelName, context.User, ModelType.Embedding);
        var lastQuestion = input["input"]?.ToString() ?? input["prompt"]?.ToString();
        telemetryService.TrackRequest(context.User, virtualModel.Name, lastQuestion, 1);

        var ollamaRequest = new JsonObject
        {
            ["model"] = backend.UnderlyingModelName,
            ["keep_alive"] = backend.Provider?.KeepAlive
        };

        if (input["input"] != null) ollamaRequest["input"] = input["input"]!.DeepClone();
        if (input["prompt"] != null) ollamaRequest["input"] = input["prompt"]!.DeepClone();

        if (virtualModel.NumCtx.HasValue || virtualModel.Temperature.HasValue || virtualModel.TopP.HasValue || virtualModel.TopK.HasValue || virtualModel.RepeatPenalty.HasValue)
        {
            var optionsNode = new JsonObject();
            if (virtualModel.NumCtx.HasValue) optionsNode["num_ctx"] = virtualModel.NumCtx.Value;
            if (virtualModel.Temperature.HasValue) optionsNode["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) optionsNode["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.TopK.HasValue) optionsNode["top_k"] = virtualModel.TopK.Value;
            if (virtualModel.RepeatPenalty.HasValue) optionsNode["repeat_penalty"] = virtualModel.RepeatPenalty.Value;
            ollamaRequest["options"] = optionsNode;
        }

        var (response, _) = await upstreamExecutor.ExecuteWithRetriesAsync(virtualModel, backend, "/api/embed", ollamaRequest.ToJsonString(), context);
        
        context.Response.StatusCode = (int)response.StatusCode;
        response.CopyHeadersTo(context.Response);
        telemetryService.TrackUpstreamResponse((int)response.StatusCode, response.IsSuccessStatusCode);
        logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for embedding request for model {Model}", context.TraceIdentifier, (int)response.StatusCode, virtualModel.Name);

        if (context.Request.Path.StartsWithSegments("/v1/embeddings"))
        {
            await openAiFormatter.FormatResponseAsync(response, context, virtualModel, false, ModelType.Embedding);
        }
        else
        {
            await ollamaFormatter.FormatResponseAsync(response, context, virtualModel, false, ModelType.Embedding);
        }
    }
}
