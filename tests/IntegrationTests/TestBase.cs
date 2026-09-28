using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

public abstract class TestBase
{
    private static int nextPort = Random.Shared.Next(20000, 25000);
    protected readonly int Port;
    protected readonly HttpClient Http;
    protected IHost? Server;

    protected TestBase()
    {
        var cookieContainer = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AllowAutoRedirect = false
        };
        Port = FindTestPort();
        Http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://localhost:{Port}")
        };
    }

    private static int FindTestPort()
    {
        // Keep each test's listening port distinct within this process. The OS's
        // ephemeral client port range can race with a port-0 availability probe.
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = Interlocked.Increment(ref nextPort);
            using var listener = new TcpListener(IPAddress.Any, candidate);
            try
            {
                listener.Start();
                return candidate;
            }
            catch (SocketException)
            {
                // Another service owns this candidate; try the next one.
            }
        }

        throw new InvalidOperationException("Could not reserve a free integration test port.");
    }

    [TestInitialize]
    public virtual async Task CreateServer()
    {
        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();
    }

    [TestCleanup]
    public virtual async Task CleanServer()
    {
        if (Server == null) return;
        await Server.StopAsync();
        Server.Dispose();
    }

    protected async Task<string> GetAntiCsrfToken(string url)
    {
        var response = await Http.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            response = await Http.GetAsync("/");
        }
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            @"<input name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)"" />");
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not find anti-CSRF token on page: {url}");
        }

        return match.Groups[1].Value;
    }

    protected async Task<HttpResponseMessage> PostForm(string url, Dictionary<string, string> data, string? tokenUrl = null, bool includeToken = true)
    {
        if (includeToken && !data.ContainsKey("__RequestVerificationToken"))
        {
            var token = await GetAntiCsrfToken(tokenUrl ?? url);
            data["__RequestVerificationToken"] = token;
        }
        return await Http.PostAsync(url, new FormUrlEncodedContent(data));
    }

    protected void AssertRedirect(HttpResponseMessage response, string expectedLocation, bool exact = true)
    {
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        var actualLocation = response.Headers.Location?.OriginalString ?? string.Empty;
        var baseUri = Http.BaseAddress?.ToString() ?? "____";

        if (actualLocation.StartsWith(baseUri))
        {
            actualLocation = actualLocation.Substring(baseUri.Length - 1); // Keep the leading slash
        }

        if (exact)
        {
            Assert.AreEqual(expectedLocation, actualLocation, $"Expected redirect to {expectedLocation}, but was {actualLocation}");
        }
        else
        {
            Assert.StartsWith(expectedLocation, actualLocation);
        }
    }

    protected async Task LoginAsAdmin()
    {
        var loginResponse = await PostForm("/Account/Login", new Dictionary<string, string>
        {
            { "EmailOrUserName", "admin@default.com" },
            { "Password", "Admin@123456!" }
        });
        Assert.AreEqual(HttpStatusCode.Found, loginResponse.StatusCode);
    }

    protected async Task<(string email, string password)> RegisterAndLoginAsync()
    {
        var email = $"test-{Guid.NewGuid()}@aiursoft.com";
        var password = "Test-Password-123";

        var registerResponse = await PostForm("/Account/Register", new Dictionary<string, string>
        {
            { "Email", email },
            { "Password", password },
            { "ConfirmPassword", password }
        });
        Assert.AreEqual(HttpStatusCode.Found, registerResponse.StatusCode);

        return (email, password);
    }

    protected T GetService<T>() where T : notnull
    {
        if (Server == null) throw new InvalidOperationException("Server is not started.");
        return Server.Services.GetRequiredService<T>();
    }

    protected async Task GrantPermissionToUser(string email, string permissionName)
    {
        using var scope = Server!.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole>>();
        var user = await userManager.FindByEmailAsync(email);

        var roleName = $"Role-{permissionName}";
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(roleName));
            await roleManager.AddClaimAsync(await roleManager.FindByNameAsync(roleName) ?? throw new Exception(),
                new System.Security.Claims.Claim(Authorization.AppPermissions.Type, permissionName));
        }

        await userManager.AddToRoleAsync(user!, roleName);
    }
}
