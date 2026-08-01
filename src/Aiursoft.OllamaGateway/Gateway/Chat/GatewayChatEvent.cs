namespace Aiursoft.OllamaGateway.Gateway.Chat;

// Provider metadata and opaque/error events are part of the stable event contract even
// when the currently registered writers do not need every field.
// ReSharper disable NotAccessedPositionalProperty.Global

/// <summary>
/// Protocol-neutral event stream emitted by provider response decoders.
/// A finite non-streaming response is represented by the same event sequence.
/// </summary>
public abstract record GatewayChatEvent;

public sealed record GatewayResponseStarted(
    string ResponseId,
    string Model,
    long CreatedAtUnixSeconds) : GatewayChatEvent;

public sealed record GatewayTextDelta(string Text) : GatewayChatEvent;

public sealed record GatewayReasoningDelta(string Text) : GatewayChatEvent;

public sealed record GatewayToolCallStarted(
    int Index,
    string Id,
    string Name) : GatewayChatEvent;

public sealed record GatewayToolArgumentsDelta(
    int Index,
    string JsonFragment) : GatewayChatEvent;

public sealed record GatewayToolCallCompleted(int Index) : GatewayChatEvent;

public sealed record GatewayUsageUpdated(
    long PromptTokens,
    long CompletionTokens) : GatewayChatEvent;

public sealed record GatewayResponseCompleted(GatewayFinishReason FinishReason) : GatewayChatEvent;

public sealed record GatewayStreamError(
    string Code,
    string Message) : GatewayChatEvent;

public sealed record GatewayOpaqueEvent(
    ProtocolDialect Dialect,
    string EventType,
    string RawData) : GatewayChatEvent;

public enum GatewayFinishReason
{
    Stop,
    Length,
    ToolCalls,
    ContentFilter,
    Error,
    Unknown
}
// ReSharper restore NotAccessedPositionalProperty.Global
