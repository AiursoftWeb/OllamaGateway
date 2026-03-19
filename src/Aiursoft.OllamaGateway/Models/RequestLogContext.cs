using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Models;

public class RequestLogContext
{
    public RequestLog Log { get; } = new()
    {
        RequestTime = DateTime.UtcNow
    };
}