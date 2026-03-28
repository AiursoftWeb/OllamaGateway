using System.Text.Json;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services.Proxy.Formatters;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy.Handlers;

public interface IOllamaChatHandler : IScopedDependency
{
    Task ProxyChatAsync(OllamaRequestModel input, HttpContext context);
    Task ProxyCompletionsAsync(OllamaRequestModel input, HttpContext context);
}

public class OllamaChatHandler(
    ILogger<OllamaChatHandler> logger,
    IModelSelectionService modelSelectionService,
    IUpstreamExecutor upstreamExecutor,
    ProxyTelemetryService telemetryService,
    OllamaResponseFormatter ollamaFormatter) : IOllamaChatHandler
{
    private static readonly JsonSerializerOptions OllamaJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task ProxyChatAsync(OllamaRequestModel input, HttpContext context)
    {
        await ProxyGenericAsync(input, context, "/api/chat", ModelType.Chat);
    }

    public async Task ProxyCompletionsAsync(OllamaRequestModel input, HttpContext context)
    {
        try
        {
            await ProxyGenericAsync(input, context, "/api/generate", ModelType.Completion);
        }
        catch (ModelNotFoundException)
        {
            await ProxyGenericAsync(input, context, "/api/generate", ModelType.Chat);
        }
    }

    private async Task ProxyGenericAsync(OllamaRequestModel input, HttpContext context, string endpoint, ModelType type)
    {
        var (virtualModel, backend) = await modelSelectionService.SelectModelAsync(input.Model, context.User, type);
        
        var messageCount = type == ModelType.Chat ? (input.Messages?.Count ?? 0) : 0;
        var lastQuestion = type == ModelType.Chat ? input.Messages?.LastOrDefault()?.Content : input.Prompt;
        
        telemetryService.TrackRequest(context.User, virtualModel.Name, lastQuestion, messageCount);

        input.Model = backend.UnderlyingModelName;
        if (backend.Provider != null)
        {
            if (virtualModel.Thinking.HasValue) input.Think = virtualModel.Thinking.Value;
            input.KeepAlive ??= backend.Provider.KeepAlive;
        }
        
        input.Options ??= new OllamaRequestOptions();
        if (virtualModel.NumCtx.HasValue) input.Options.NumCtx = virtualModel.NumCtx;
        if (virtualModel.Temperature.HasValue) input.Options.Temperature = virtualModel.Temperature;
        if (virtualModel.TopP.HasValue) input.Options.TopP = virtualModel.TopP;
        if (virtualModel.TopK.HasValue) input.Options.TopK = virtualModel.TopK;
        if (virtualModel.NumPredict.HasValue) input.Options.NumPredict = virtualModel.NumPredict;
        if (virtualModel.RepeatPenalty.HasValue) input.Options.RepeatPenalty = virtualModel.RepeatPenalty;

        var requestJson = JsonSerializer.Serialize(input, OllamaJsonOptions);

        var (response, _) = await upstreamExecutor.ExecuteWithRetriesAsync(virtualModel, backend, endpoint, requestJson, context);
        
        context.Response.StatusCode = (int)response.StatusCode;
        response.CopyHeadersTo(context.Response);
        telemetryService.TrackUpstreamResponse((int)response.StatusCode, response.IsSuccessStatusCode);
        logger.LogInformation("[{TraceId}] Received response from upstream: {StatusCode} for {Type} request for model {Model}", context.TraceIdentifier, (int)response.StatusCode, type, virtualModel.Name);

        await ollamaFormatter.FormatResponseAsync(response, context, virtualModel, input.Stream == true, type);
    }
}
