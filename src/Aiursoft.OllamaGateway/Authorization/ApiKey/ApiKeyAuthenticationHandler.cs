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
        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var authHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKeyStr = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(apiKeyStr))
        {
            return AuthenticateResult.Fail("Empty API Key.");
        }

        var apiKey = await dbContext.ApiKeys
            .Include(a => a.User)
            .FirstOrDefaultAsync(a => a.Key == apiKeyStr);

        if (apiKey == null || apiKey.User == null)
        {
            return AuthenticateResult.Fail("Invalid API Key.");
        }

        var isAllowed = await rateLimitService.IsAllowedAsync(apiKey);
        if (!isAllowed)
        {
            return AuthenticateResult.Fail("Rate limit exceeded.");
        }

        apiKey.LastUsed = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

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
