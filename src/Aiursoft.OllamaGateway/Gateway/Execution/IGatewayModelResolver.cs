using System.Security.Claims;

namespace Aiursoft.OllamaGateway.Gateway.Execution;

public interface IGatewayModelResolver
{
    Task<GatewayModelResolution> ResolveChatAsync(
        string? requestedModel,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);

    Task<GatewayModelResolution> ResolveEmbeddingAsync(
        string? requestedModel,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);
}
