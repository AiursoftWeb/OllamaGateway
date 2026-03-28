using Aiursoft.OllamaGateway.Entities;
using Aiursoft.Scanner.Abstractions;
using System.Security.Claims;

namespace Aiursoft.OllamaGateway.Services;

public interface IModelSelectionService : IScopedDependency
{
    Task<(VirtualModel Model, VirtualModelBackend Backend)> SelectModelAsync(string modelName, ClaimsPrincipal user, ModelType type);
}
