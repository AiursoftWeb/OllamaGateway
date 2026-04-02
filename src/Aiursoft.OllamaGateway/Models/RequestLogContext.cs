using System.Diagnostics.CodeAnalysis;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Models;
[ExcludeFromCodeCoverage]
public class RequestLogContext
{
    public RequestLog Log { get; } = new()
    {
        RequestTime = DateTime.UtcNow
    };
}