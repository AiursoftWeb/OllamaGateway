namespace Aiursoft.OllamaGateway.Entities;

public class RequestLog
{
    public string IP { get; set; } = string.Empty;
    public int ConversationMessageCount { get; set; }
    public string LastQuestion { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Success { get; set; }
    public double Duration { get; set; }
    public string Answer { get; set; } = string.Empty;
    public DateTime RequestTime { get; set; }

    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string UserAgent { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ApiKeyName { get; set; } = string.Empty;
}
