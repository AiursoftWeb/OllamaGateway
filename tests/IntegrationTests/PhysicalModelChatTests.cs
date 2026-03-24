using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.DbTools;
using Aiursoft.OllamaGateway.Authorization;
using Aiursoft.OllamaGateway.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.OllamaGateway.Tests.IntegrationTests;

[TestClass]
public class PhysicalModelChatTests : TestBase
{
    private const string TestApiKey = "physical-chat-test-key";
    private const string PhysicalModelName = "llama3-direct";

    [TestInitialize]
    public override async Task CreateServer()
    {
        TestStartup.MockClickhouse.Invocations.Clear();
        TestStartup.MockClickhouse.Setup(c => c.Enabled).Returns(false);
        TestStartup.MockOllamaService.Invocations.Clear();
        MockUpstreamState.Reset();

        Server = await AppAsync<TestStartup>([], port: Port);
        await Server.UpdateDbAsync<TemplateDbContext>();
        await Server.SeedAsync();
        await Server.StartAsync();
    }

    [TestMethod]
    public async Task TestChatWithPhysicalModel_Success()
    {
        // 1. Setup provider and API key for a user WITH permission
        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            
            // Add permission to the user via claims
            db.UserClaims.Add(new IdentityUserClaim<string>
            {
                UserId = user.Id,
                ClaimType = AppPermissions.Type,
                ClaimValue = AppPermissionNames.CanChatWithUnderlyingModels
            });
            
            db.ApiKeys.Add(new ApiKey { Name = "Direct Key", Key = TestApiKey, UserId = user.Id });
            
            var provider = new OllamaProvider { Name = "DirectProvider", BaseUrl = "http://direct-ollama:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }

        // 2. Mock Upstream Response
        MockUpstreamState.Handler = (_, _) =>
        {
            var response = new JsonObject
            {
                ["model"] = PhysicalModelName,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Direct response" },
                ["done"] = true
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            });
        };

        // 3. Request Chat with physical model
        var chatRequest = new
        {
            model = $"physical_{providerId}_{PhysicalModelName}",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream = false
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(chatRequest), System.Text.Encoding.UTF8, "application/json");
        
        var response = await Http.SendAsync(request);
        
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonNode.Parse(content);
        Assert.AreEqual($"physical_{providerId}_{PhysicalModelName}", result?["model"]?.ToString());
        Assert.AreEqual("Direct response", result?["message"]?["content"]?.ToString());
    }

    [TestMethod]
    public async Task TestChatWithPhysicalModel_NoPermission()
    {
        // 1. Setup provider and API key for a user WITHOUT permission
        int providerId;
        const string NoPermKey = "no-perm-key";
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            
            // Create a new user without any roles/permissions initially
            var user = new User 
            { 
                UserName = "noperm@aiursoft.com", 
                Email = "noperm@aiursoft.com",
                DisplayName = "No Permission User"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            db.ApiKeys.Add(new ApiKey { Name = "No Perm Key", Key = NoPermKey, UserId = user.Id });
            
            var provider = new OllamaProvider { Name = "DirectProvider2", BaseUrl = "http://direct-ollama:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }

        // 2. Request Chat with physical model
        var chatRequest = new
        {
            model = $"physical_{providerId}_{PhysicalModelName}",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream = false
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", NoPermKey);
        request.Content = new StringContent(JsonSerializer.Serialize(chatRequest), System.Text.Encoding.UTF8, "application/json");
        
        var response = await Http.SendAsync(request);
        
        // Should be 403 Forbidden because the user doesn't have CanChatWithUnderlyingModels
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
