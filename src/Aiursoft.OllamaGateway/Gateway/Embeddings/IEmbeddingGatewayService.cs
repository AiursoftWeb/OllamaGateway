using System.Text.Json.Nodes;
using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Embeddings;

public interface IEmbeddingGatewayService
{
    Task ExecuteAsync(
        ProtocolDialect clientDialect,
        JsonObject clientBody,
        VirtualModel virtualModel,
        VirtualModelBackend initialBackend,
        HttpContext httpContext);
}
