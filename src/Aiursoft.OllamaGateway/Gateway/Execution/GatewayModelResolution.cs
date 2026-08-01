using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Gateway.Execution;

public sealed record GatewayModelResolution(
    VirtualModel? VirtualModel,
    VirtualModelBackend? Backend,
    GatewayModelResolutionError? Error)
{
    public bool IsSuccess => VirtualModel != null && Backend?.Provider != null && Error == null;

    public static GatewayModelResolution Success(
        VirtualModel virtualModel,
        VirtualModelBackend backend)
    {
        return new GatewayModelResolution(virtualModel, backend, null);
    }

    public static GatewayModelResolution Failure(int statusCode, string message)
    {
        return new GatewayModelResolution(null, null, new GatewayModelResolutionError(statusCode, message));
    }
}

public sealed record GatewayModelResolutionError(int StatusCode, string Message);
