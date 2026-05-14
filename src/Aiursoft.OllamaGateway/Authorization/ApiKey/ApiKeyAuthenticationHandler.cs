using System.Security.Claims;
using System.Text.Encodings.Web;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiursoft.OllamaGateway.Authorization.ApiKey;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TemplateDbContext dbContext,
    IUserClaimsPrincipalFactory<User> userClaimsPrincipalFactory,
    Services.RateLimitService rateLimitService)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKeyStr = null;
        if (Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            var authHeader = authHeaderValues.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                apiKeyStr = authHeader["Bearer ".Length..].Trim();
            }
            else
            {
                apiKeyStr = authHeader.Trim();
            }
        }
        else if (Request.Headers.TryGetValue("x-api-key", out var xApiKeyValues))
        {
            apiKeyStr = xApiKeyValues.ToString().Trim();
        }
        else if (Request.Headers.TryGetValue("api-key", out var apiKeyValues))
        {
            apiKeyStr = apiKeyValues.ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(apiKeyStr))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKey = await dbContext.ApiKeys
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Key == apiKeyStr);

        if (apiKey == null || apiKey.User == null)
        {
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        if (apiKey.ExpirationTime < DateTime.UtcNow)
        {
            return AuthenticateResult.Fail("API Key expired.");
        }

        var isAllowed = await rateLimitService.IsAllowedAsync(apiKey);
        if (!isAllowed)
        {
            return AuthenticateResult.Fail("Rate limit exceeded.");
        }

        // LastUsed is persisted in batch by UsageFlushService every 3 minutes.
        // Writing it here would add a synchronous DB round-trip to every request.

        var principal = await userClaimsPrincipalFactory.CreateAsync(apiKey.User);
        var identity = (ClaimsIdentity)principal.Identity!;
        
        identity.AddClaims(new List<Claim>
        {
            new("ApiKeyId", apiKey.Id.ToString()),
            new("ApiKeyName", apiKey.Name)
        });

        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
