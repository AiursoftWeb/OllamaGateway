using System.Runtime.CompilerServices;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.OllamaGateway.Services;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

public sealed class ChatCrossDialectResponseWriter(
    IEnumerable<IChatProviderResponseDecoder> providerDecoders,
    IEnumerable<IChatClientResponseWriter> clientWriters,
    GatewayRequestTracker tracker, RequestLogContext logContext) : IChatCrossDialectResponseWriter
{
    public async Task WriteAsync(
        ProtocolDialect clientDialect,
        BackendProtocol providerProtocol,
        HttpResponseMessage upstreamResponse,
        VirtualModel virtualModel,
        bool streaming,
        HttpContext httpContext)
    {
        var decoder = providerDecoders.Single(item => item.Protocol == providerProtocol);
        var writer = clientWriters.Single(item => item.Dialect == clientDialect);
        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
        var events = decoder.DecodeAsync(responseStream, streaming, httpContext.RequestAborted);
        await writer.WriteTranslatedAsync(
            Observe(events, httpContext.RequestAborted),
            virtualModel,
            streaming,
            httpContext.Response,
            httpContext.RequestAborted);
    }

    private async IAsyncEnumerable<GatewayChatEvent> Observe(
        IAsyncEnumerable<GatewayChatEvent> events,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var answer = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            switch (item)
            {
                case GatewayTextDelta text:
                    tracker.ObserveAnswer(text.Text);
                    answer.Append(text.Text);
                    break;
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
