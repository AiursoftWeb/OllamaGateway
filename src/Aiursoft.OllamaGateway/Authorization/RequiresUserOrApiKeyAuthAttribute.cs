using Aiursoft.OllamaGateway.Services;
using Aiursoft.OllamaGateway.Services.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Aiursoft.OllamaGateway.Authorization;

public class RequiresUserOrApiKeyAuthAttribute : TypeFilterAttribute
{
    public RequiresUserOrApiKeyAuthAttribute() : base(typeof(RequiresUserOrApiKeyAuthFilter))
    {
    }
}

public class RequiresUserOrApiKeyAuthFilter(GlobalSettingsService globalSettingsService) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var isAuthorized = false;

        // 1. If already authenticated by cookie or other middleware
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            isAuthorized = true;
        }

        // 2. Manually trigger ApiKey authentication
        if (!isAuthorized)
        {
            var result = await context.HttpContext.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
            if (result.Succeeded && result.Principal != null)
            {
                // Update the User property so subsequent code can access claims
                context.HttpContext.User = result.Principal;
                isAuthorized = true;
            }
        }

        // 3. Check global setting for anonymous access
        if (!isAuthorized)
        {
            isAuthorized = await globalSettingsService.GetAllowAnonymousApiCallAsync();
        }

        if (!isAuthorized)
        {
            context.Result = new ObjectResult("Unauthorized. Please provide a valid Bearer token or enable anonymous access.")
            {
                StatusCode = 401
            };
        }
    }
}
