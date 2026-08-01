using System.Runtime.CompilerServices;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class ChatCrossDialectResponseWriter(
    IEnumerable<IChatProviderResponseDecoder> providerDecoders,
    IEnumerable<IChatClientResponseWriter> clientWriters,
    RequestLogContext logContext) : IChatCrossDialectResponseWriter
{
    public async Task WriteAsync(
        ProtocolDialect clientDialect,
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        VirtualModelBackend actualBackend,
        bool streaming,
        HttpContext httpContext)
    {
        var providerType = actualBackend.Provider?.ProviderType
                           ?? throw new InvalidOperationException("Cannot decode a chat response without a provider.");
        var decoder = providerDecoders.Single(item => item.ProviderType == providerType);
        var writer = clientWriters.Single(item => item.Dialect == clientDialect);
        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
        var events = decoder.DecodeAsync(responseStream, streaming, httpContext.RequestAborted);
        await writer.WriteTranslatedAsync(
            Observe(events, logContext, httpContext.RequestAborted),
            virtualModel,
            streaming,
            httpContext.Response,
            httpContext.RequestAborted);
    }

    private static async IAsyncEnumerable<GatewayChatEvent> Observe(
        IAsyncEnumerable<GatewayChatEvent> events,
        RequestLogContext logContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayTextDelta text: answer.Append(text.Text); break;
                case GatewayReasoningDelta reasoning: thinking.Append(reasoning.Text); break;
                case GatewayUsageUpdated usage:
                    logContext.Log.PromptTokens = (int)usage.PromptTokens;
                    logContext.Log.CompletionTokens = (int)usage.CompletionTokens;
                    logContext.Log.TotalTokens = (int)(usage.PromptTokens + usage.CompletionTokens);
                    break;
            }
            yield return item;
        }
        logContext.Log.Answer = answer.ToString();
        logContext.Log.Thinking = thinking.ToString();
    }
}
