using System.Text.Json.Nodes;

namespace Aiursoft.OllamaGateway.Gateway.Chat;

/// <summary>
/// A client request after semantic decoding. OriginalBody is retained for the
/// same-dialect fast path so vendor extensions unknown to the gateway survive.
/// </summary>
public sealed record DecodedChatRequest(
    ProtocolDialect SourceDialect,
    GatewayChatRequest Request,
    JsonObject OriginalBody);
