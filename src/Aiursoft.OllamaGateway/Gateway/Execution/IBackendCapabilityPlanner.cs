using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Execution;

public interface IBackendCapabilityPlanner
{
    bool Supports(VirtualModelBackend backend, GatewayCapability capability);
}
