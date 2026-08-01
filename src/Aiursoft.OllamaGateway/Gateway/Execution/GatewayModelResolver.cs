using System.Security.Claims;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.OllamaGateway.Gateway.Execution;

public sealed class GatewayModelResolver(
    TemplateDbContext dbContext,
    GlobalSettingsService globalSettingsService,
    IModelSelector modelSelector) : IGatewayModelResolver
{
    public async Task<GatewayModelResolution> ResolveChatAsync(
        string? requestedModel,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var modelName = string.IsNullOrWhiteSpace(requestedModel)
            ? await globalSettingsService.GetDefaultChatModelAsync()
            : requestedModel;

        var physicalResolution = await TryResolvePhysicalModelAsync(modelName, user, cancellationToken);
        if (physicalResolution != null)
        {
            return physicalResolution;
        }

        return await ResolveVirtualModelAsync(modelName, ModelType.Chat, cancellationToken);
    }

    public async Task<GatewayModelResolution> ResolveEmbeddingAsync(
        string? requestedModel,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        _ = user;
        var modelName = string.IsNullOrWhiteSpace(requestedModel)
            ? await globalSettingsService.GetDefaultEmbeddingModelAsync()
            : requestedModel;

        return await ResolveVirtualModelAsync(modelName, ModelType.Embedding, cancellationToken);
    }

    private async Task<GatewayModelResolution?> TryResolvePhysicalModelAsync(
        string modelName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (!modelName.StartsWith("physical_", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = modelName.Split('_');
        if (parts.Length < 3 || !int.TryParse(parts[1], out var providerId))
        {
            return null;
        }

        if (!user.HasClaim(AppPermissions.Type, AppPermissionNames.CanChatWithUnderlyingModels))
        {
            return GatewayModelResolution.Failure(
                StatusCodes.Status403Forbidden,
                "Forbidden. You don't have permission to chat with underlying models.");
        }

        var provider = await dbContext.OllamaProviders.FindAsync([providerId], cancellationToken);
        if (provider == null)
        {
            return GatewayModelResolution.Failure(
                StatusCodes.Status404NotFound,
                $"Provider with ID {providerId} not found.");
        }

        var underlyingModelName = string.Join('_', parts.Skip(2));
        var virtualModel = new VirtualModel
        {
            Name = modelName,
            MaxRetries = 1,
            RequestTimeoutSeconds = 600
        };
        var backend = new VirtualModelBackend
        {
            Provider = provider,
            UnderlyingModelName = underlyingModelName,
            ProviderId = providerId
        };

        return GatewayModelResolution.Success(virtualModel, backend);
    }

    private async Task<GatewayModelResolution> ResolveVirtualModelAsync(
        string modelName,
        ModelType modelType,
        CancellationToken cancellationToken)
    {
        var virtualModel = await dbContext.VirtualModels
            .Include(model => model.VirtualModelBackends)
            .ThenInclude(backend => backend.Provider)
            .FirstOrDefaultAsync(
                model => model.Name == modelName && model.Type == modelType,
                cancellationToken);

        if (virtualModel == null)
        {
            var modelKind = modelType == ModelType.Embedding ? "Embedding model" : "Model";
            return GatewayModelResolution.Failure(
                StatusCodes.Status404NotFound,
                $"{modelKind} '{modelName}' not found in gateway.");
        }

        var backend = modelSelector.SelectBackend(virtualModel);
        if (backend?.Provider == null)
        {
            return GatewayModelResolution.Failure(
                StatusCodes.Status503ServiceUnavailable,
                $"No available backend for model '{modelName}'.");
        }

        return GatewayModelResolution.Success(virtualModel, backend);
    }
}
