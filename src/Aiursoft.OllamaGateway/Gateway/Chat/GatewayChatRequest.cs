using System.Collections.ObjectModel;
using Aiursoft.OllamaGateway.Gateway.Execution;

// Some semantic fields are intentionally retained for adapters that are not implemented yet.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace Aiursoft.OllamaGateway.Gateway.Chat;

/// <summary>
/// Protocol-neutral semantic representation of a chat request.
/// Wire-specific JSON stays in protocol adapters rather than leaking into this model.
/// </summary>
public sealed record GatewayChatRequest(
    bool Stream,
    IReadOnlyList<GatewayChatMessage> Messages,
    GatewayChatOptions Options,
    IReadOnlyList<GatewayToolDefinition> Tools,
    GatewayToolChoice? ToolChoice = null,
    string? Instructions = null,
    GatewayCapability RequiredCapabilities = GatewayCapability.ChatCompletion,
    IReadOnlyDictionary<string, string>? Extensions = null)
{
    public IReadOnlyDictionary<string, string> Extensions { get; init; } =
        Extensions ?? ReadOnlyDictionary<string, string>.Empty;
}

public sealed record GatewayChatMessage(
    string Role,
    IReadOnlyList<GatewayContentPart> Content);

public abstract record GatewayContentPart;

public sealed record GatewayTextContent(string Text) : GatewayContentPart;

public sealed record GatewayImageContent(
    string Data,
    string? MediaType,
    bool IsUrl) : GatewayContentPart;

public sealed record GatewayReasoningContent(
    string Text,
    string? Signature = null) : GatewayContentPart;

public sealed record GatewayToolCallContent(
    string Id,
    string Name,
    string ArgumentsJson) : GatewayContentPart;

public sealed record GatewayToolResultContent(
    string ToolCallId,
    string Content,
    bool IsError = false) : GatewayContentPart;

public sealed record GatewayOpaqueContent(
    ProtocolDialect Dialect,
    string Type,
    string RawJson) : GatewayContentPart;

public sealed record GatewayToolDefinition(
    string Name,
    string Description,
    string InputSchemaJson);

public enum GatewayToolChoiceMode
{
    Auto,
    None,
    Required,
    Function
}

public sealed record GatewayToolChoice(
    GatewayToolChoiceMode Mode,
    string? FunctionName = null);

public sealed record GatewayChatOptions(
    double? Temperature = null,
    double? TopP = null,
    int? TopK = null,
    int? MaxTokens = null,
    int? ContextSize = null,
    double? RepeatPenalty = null,
    bool? Thinking = null,
    string? KeepAlive = null,
    string? ReasoningEffort = null,
    string? StructuredOutputJson = null);
// ReSharper restore NotAccessedPositionalProperty.Global
