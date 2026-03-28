using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;

namespace Aiursoft.OllamaGateway.Services.Proxy;

public interface IUpstreamExecutor : IScopedDependency
{
    Task<(HttpResponseMessage Response, VirtualModelBackend FinalBackend)> ExecuteWithRetriesAsync(
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        string endpoint,
        string requestJson,
        HttpContext context);
}
