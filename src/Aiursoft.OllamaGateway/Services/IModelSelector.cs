using Aiursoft.OllamaGateway.Entities;

namespace Aiursoft.OllamaGateway.Services;

public interface IModelSelector
{
    VirtualModelBackend? SelectBackend(VirtualModel virtualModel);
    void ReportSuccess(int backendId);
    void ReportFailure(int backendId);
    DateTime? GetBanUntil(int backendId);
}
