using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Models;
using Aiursoft.Scanner.Abstractions;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Aiursoft.OllamaGateway.Services;

public class ModelSelectionService(
    TemplateDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    IModelSelector modelSelector) : IModelSelectionService, IScopedDependency
{
    public async Task<(VirtualModel Model, VirtualModelBackend Backend)> SelectModelAsync(string modelName, ClaimsPrincipal user, ModelType type)
    {
        var modelToUse = string.IsNullOrWhiteSpace(modelName)
            ? await globalSettingsService.GetDefaultChatModelAsync()
            : modelName;

        VirtualModel? virtualModel = null;
        VirtualModelBackend? backend = null;

        if (modelToUse.StartsWith("physical_"))
        {
            var parts = modelToUse.Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[1], out var providerId))
            {
                if (!user.HasClaim(AppPermissions.Type, AppPermissionNames.CanChatWithUnderlyingModels))
                {
                    throw new ForbiddenException("Forbidden. You don't have permission to chat with underlying models.");
                }

                var provider = await dbContext.OllamaProviders.FindAsync(providerId);
                if (provider == null)
                {
                    throw new ModelNotFoundException($"Provider with ID {providerId} not found.");
                }

                var underlyingModelName = string.Join('_', parts.Skip(2));
                virtualModel = new VirtualModel
                {
                    Name = modelToUse,
                    MaxRetries = 1,
                    HealthCheckTimeout = 30,
                    Type = type
                };
                backend = new VirtualModelBackend
                {
                    Provider = provider,
                    UnderlyingModelName = underlyingModelName,
                    ProviderId = providerId
                };
            }
        }

        if (virtualModel == null)
        {
            virtualModel = await dbContext.VirtualModels
                .Include(m => m.VirtualModelBackends).ThenInclude(b => b.Provider)
                .FirstOrDefaultAsync(m => m.Name == modelToUse && m.Type == type);

            if (virtualModel == null)
            {
                throw new ModelNotFoundException(modelToUse);
            }

            backend = modelSelector.SelectBackend(virtualModel);
        }

        if (backend == null || backend.Provider == null)
        {
            throw new NoAvailableBackendException(virtualModel.Name);
        }

        return (virtualModel, backend);
    }
}
