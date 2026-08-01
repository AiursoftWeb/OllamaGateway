namespace Aiursoft.OllamaGateway.Gateway.Framing;

public sealed record SseFrame(
    string? EventType,
    string Data,
    string? Id,
    int? RetryMilliseconds,
    IReadOnlyList<string> Comments);
