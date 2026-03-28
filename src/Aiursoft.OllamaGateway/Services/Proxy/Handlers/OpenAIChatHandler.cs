using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services.Proxy.Formatters;
using Aiursoft.OllamaGateway.Services.Proxy.Translators;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy.Handlers;

public interface IOpenAIChatHandler : IScopedDependency
{
    Task ProxyOpenAIChatAsync(JsonObject clientJson, HttpContext context);
}

public class OpenAIChatHandler(
    IModelSelectionService modelSelectionService,
    IUpstreamExecutor upstreamExecutor,
    ProxyTelemetryService telemetryService,
    OpenAIResponseFormatter openAiFormatter) : IOpenAIChatHandler
{
    public async Task ProxyOpenAIChatAsync(JsonObject clientJson, HttpContext context)
    {
        var isStream = clientJson["stream"]?.GetValue<bool>() ?? false;
        var modelName = clientJson["model"]?.ToString() ?? string.Empty;
        var (virtualModel, backend) = await modelSelectionService.SelectModelAsync(modelName, context.User, ModelType.Chat);

        var messagesArray = clientJson["messages"]?.AsArray();
        var messageCount = messagesArray?.Count ?? 0;
        var lastQuestion = messagesArray?.LastOrDefault()?["content"]?.ToString() ?? string.Empty;
        telemetryService.TrackRequest(context.User, virtualModel.Name, lastQuestion, messageCount);

        var ollamaRequest = OpenAIRequestTranslator.TranslateChatRequest(clientJson, backend.UnderlyingModelName, virtualModel.Thinking ?? false);

        if (virtualModel.Temperature.HasValue || virtualModel.TopP.HasValue || virtualModel.NumPredict.HasValue || virtualModel.NumCtx.HasValue)
        {
            var optionsNode = ollamaRequest["options"]?.AsObject() ?? new JsonObject();
            if (virtualModel.Temperature.HasValue) optionsNode["temperature"] = virtualModel.Temperature.Value;
            if (virtualModel.TopP.HasValue) optionsNode["top_p"] = virtualModel.TopP.Value;
            if (virtualModel.NumPredict.HasValue) optionsNode["num_predict"] = virtualModel.NumPredict.Value;
            if (virtualModel.NumCtx.HasValue) optionsNode["num_ctx"] = virtualModel.NumCtx.Value;
            ollamaRequest["options"] = optionsNode;
        }

        var (response, _) = await upstreamExecutor.ExecuteWithRetriesAsync(virtualModel, backend, "/api/chat", ollamaRequest.ToJsonString(), context);
        
        context.Response.StatusCode = (int)response.StatusCode;
        telemetryService.TrackUpstreamResponse((int)response.StatusCode, response.IsSuccessStatusCode);
        
        await openAiFormatter.FormatResponseAsync(response, context, virtualModel, isStream, ModelType.Chat);
    }
}
