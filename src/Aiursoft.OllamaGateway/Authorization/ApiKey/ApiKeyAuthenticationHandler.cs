using System.Security.Claims;
using System.Text.Encodings.Web;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.AspNetCore.Authentication;
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
    TemplateDbContext dbContext)
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

        apiKey.LastUsed = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.UserId),
            new(ClaimTypes.Name, apiKey.User.UserName ?? string.Empty),
            new("ApiKeyId", apiKey.Id.ToString()),
            new("ApiKeyName", apiKey.Name)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
