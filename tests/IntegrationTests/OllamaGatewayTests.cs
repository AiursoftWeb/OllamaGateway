using System.Net;
using System.Security.Claims;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Entities;
using Aiursoft.OllamaGateway.Services;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;
using Moq;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class OllamaGatewayTests : TestBase
{
    [TestInitialize]
    public override async Task CreateServer()
    {
        // Setup mocks before creating server
        TestStartup.MockClickhouse.Reset();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);

        TestStartup.MockOllamaService.Reset();
        TestStartup.MockOllamaService.Setup(s => s.GetUnderlyingModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<string> { "llama3.2", "nomic-embed-text" });
        
        TestStartup.MockOllamaService.Setup(s => s.GetDetailedModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaModel> 
            { 
                new OllamaService.OllamaModel { Name = "llama3.2", Size = 1024 * 1024 * 1024L },
                new OllamaService.OllamaModel { Name = "nomic-embed-text", Size = 512 * 1024 * 1024L }
            });

        TestStartup.MockOllamaService.Setup(s => s.GetRunningModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<OllamaService.OllamaRunningModel>
            {
                new OllamaService.OllamaRunningModel { Name = "llama3.2" }
            });

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();
    }

    [TestMethod]
    public async Task TestProxyAuthWithApiKey()
    {
        // 1. Setup Data
        string apiKeyStr = "test-token-123";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            db.ApiKeys.Add(new ApiKey { Name = "Unit Test Key", Key = apiKeyStr, UserId = user.Id });
            await db.SaveChangesAsync();
        }

        // 2. Request tags without auth
        var noAuthResponse = await Http.GetAsync("/api/tags");
        // ProxyController now returns 401 manually instead of 302 redirect for API
        Assert.AreEqual(HttpStatusCode.Unauthorized, noAuthResponse.StatusCode);

        // 3. Request with API Key
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKeyStr);
        var authResponse = await Http.SendAsync(request);
        
        Assert.AreEqual(HttpStatusCode.OK, authResponse.StatusCode);
    }

    [TestMethod]
    public async Task TestAnonymousProxyCall()
    {
        // 1. Ensure anonymous is disabled (default)
        var noAuthResponse = await Http.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.Unauthorized, noAuthResponse.StatusCode);

        // 2. Enable anonymous call via Global Settings
        using (var scope = Server!.Services.CreateScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settings.UpdateSettingAsync(Configuration.SettingsMap.AllowAnonymousApiCall, "True");
        }

        // 3. Request tags again without auth -> Should be OK
        var anonymousResponse = await Http.GetAsync("/api/tags");
        Assert.AreEqual(HttpStatusCode.OK, anonymousResponse.StatusCode);
    }

    [TestMethod]
    public async Task TestProviderWorkflow()
    {
        await LoginAsAdmin();

        // 1. Create Provider
        var createResponse = await PostForm("/OllamaProviders/Create", new Dictionary<string, string>
        {
            { "Name", "Test Provider" },
            { "BaseUrl", "http://localhost:11434" }
        });
        AssertRedirect(createResponse, "/OllamaProviders", exact: false);

        // 2. Verify in list
        var indexResponse = await Http.GetAsync("/OllamaProviders");
        var indexHtml = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("Test Provider", indexHtml);
        Assert.Contains("http://localhost:11434", indexHtml);
    }

    [TestMethod]
    public async Task TestVirtualModelWorkflow()
    {
        await LoginAsAdmin();

        // 1. Need a provider first
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OllamaProviders.Add(new OllamaProvider { Name = "Default", BaseUrl = "http://localhost:11434" });
            await db.SaveChangesAsync();
        }
        
        var provider = await GetService<TemplateDbContext>().OllamaProviders.FirstAsync();

        // 2. Create Virtual Chat Model
        var createResponse = await PostForm("/VirtualModels/Create", new Dictionary<string, string>
        {
            { "Name", "my-virtual-model:latest" },
            { "UnderlyingModel", "llama3.2" },
            { "ProviderId", provider.Id.ToString() },
            { "Type", ModelType.Chat.ToString() },
            { "Temperature", "0.5" }
        });
        AssertRedirect(createResponse, "/VirtualModels", exact: false);

        // 3. Verify in list
        var indexResponse = await Http.GetAsync("/VirtualModels");
        var indexHtml = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("my-virtual-model:latest", indexHtml);
        Assert.Contains("llama3.2", indexHtml);
        Assert.Contains("Temp: 0.5", indexHtml);
    }

    [TestMethod]
    public async Task TestPhysicalModelsPage()
    {
        await LoginAsAdmin();

        // 1. Add provider
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.OllamaProviders.Add(new OllamaProvider { Name = "Active Server", BaseUrl = "http://localhost:11434" });
            await db.SaveChangesAsync();
        }

        // 2. View physical models
        var response = await Http.GetAsync("/UnderlyingModels/Index");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        
        Assert.Contains("llama3.2", html);
        Assert.Contains("nomic-embed-text", html);
        Assert.Contains("Running", html); 
        Assert.Contains("Idle", html);    
    }

    [TestMethod]
    public async Task TestApiKeyManagement()
    {
        var (email, password) = await RegisterAndLoginAsync(); 
        
        // Grant permissions to this test user
        using (var scope = Server!.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var user = await userManager.FindByEmailAsync(email);
            
            var roleName = "TestUserRole";
            await roleManager.CreateAsync(new IdentityRole(roleName));
            await roleManager.AddClaimAsync(await roleManager.FindByNameAsync(roleName) ?? throw new Exception(), 
                new Claim(Authorization.AppPermissions.Type, Authorization.AppPermissionNames.CanManageApiKeys));
            
            await userManager.AddToRoleAsync(user!, roleName);
        }

        // RE-LOGIN to refresh claims in cookie
        await PostForm("/Account/Login", new Dictionary<string, string>
        {
            { "EmailOrUserName", email },
            { "Password", password }
        });

        // 1. Create API Key
        var createResponse = await PostForm("/ApiKeys/Create", new Dictionary<string, string>
        {
            { "Name", "My Laptop" }
        });
        AssertRedirect(createResponse, "/ApiKeys", exact: false);

        // 2. Verify key exists and is shown ONCE
        var indexResponse = await Http.GetAsync("/ApiKeys");
        var html = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("My Laptop", html);
        var db = GetService<TemplateDbContext>();
        var key = await db.ApiKeys.OrderByDescending(k => k.Id).FirstAsync();
        Assert.Contains(key.Key, html); // Shown once from TempData
        
        var secondIndexResponse = await Http.GetAsync("/ApiKeys");
        var secondHtml = await secondIndexResponse.Content.ReadAsStringAsync();
        Assert.IsFalse(secondHtml.Contains(key.Key)); // Gone after refresh
        Assert.Contains(key.Key[..4] + "...", secondHtml); // Masked instead

        // 3. Verify Usage page
        var usageResponse = await Http.GetAsync($"/ApiKeys/Usage/{key.Id}");
        usageResponse.EnsureSuccessStatusCode();
        var usageHtml = await usageResponse.Content.ReadAsStringAsync();
        Assert.Contains("API Key Usage Guide", usageHtml);
        Assert.Contains("My Laptop", usageHtml);
        Assert.IsFalse(usageHtml.Contains(key.Key)); // Must be masked
        Assert.Contains(key.Key[..4] + "...", usageHtml); // Masked key
        Assert.Contains("curl", usageHtml);
        Assert.Contains("/api/chat", usageHtml);
        Assert.Contains("/api/embed", usageHtml);
        Assert.Contains("why is the sky blue?", usageHtml);
    }

    [TestMethod]
    public async Task TestChatPlayground()
    {
        await LoginAsAdmin();

        // 1. Setup Data
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var provider = new OllamaProvider { Name = "Provider", BaseUrl = "http://localhost:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            db.VirtualModels.Add(new VirtualModel 
            { 
                Name = "chat-model:latest", 
                UnderlyingModel = "llama3.2", 
                ProviderId = provider.Id,
                Type = ModelType.Chat
            });
            await db.SaveChangesAsync();
        }

        // 2. Access Playground
        var response = await Http.GetAsync("/ChatPlayground");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        
        Assert.Contains("Chat Playground", html);
        Assert.Contains("Chatting with", html);
        Assert.Contains("chat-model:latest", html);
    }
}
