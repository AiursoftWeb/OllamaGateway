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
    public async Task TestChatOptions_SnakeCaseKeysAreDeserializedAndForwardedToUpstream()
    {
        // Regression test: Newtonsoft.Json DefaultContractResolver was not matching snake_case keys
        // (e.g. "num_ctx", "top_p", "top_k") against PascalCase C# properties, silently dropping them.
        // After adding [JsonProperty("...")] attributes the fix should pass these assertions.

        const string optionsKey = "options-fwd-test-key";

        int providerId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var user = await db.Users.FirstAsync();
            db.UserClaims.Add(new IdentityUserClaim<string>
            {
                UserId = user.Id,
                ClaimType = AppPermissions.Type,
                ClaimValue = AppPermissionNames.CanChatWithUnderlyingModels
            });
            db.ApiKeys.Add(new ApiKey { Name = "Options Fwd Key", Key = optionsKey, UserId = user.Id });
            var provider = new OllamaProvider { Name = "OptionsFwdProvider", BaseUrl = "http://options-fwd:11434" };
            db.OllamaProviders.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
        }

        MockUpstreamState.Handler = (_, _) =>
        {
            var reply = new JsonObject
            {
                ["model"] = PhysicalModelName,
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "ok" },
                ["done"] = true
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            });
        };

        // Send with snake_case option keys — these were the ones silently dropped before the fix
        var chatRequest = new
        {
            model = $"physical_{providerId}_{PhysicalModelName}",
            messages = new[] { new { role = "user", content = "Hello" } },
            stream = false,
            options = new
            {
                num_ctx    = 32768,
                top_p      = 0.9,
                top_k      = 40,
                temperature = 0.7
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", optionsKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(chatRequest),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await Http.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // The critical assertion: what actually reached Ollama?
        Assert.IsNotNull(MockUpstreamState.LastRequestBody, "Upstream should have received a request body");
        var forwarded = JsonNode.Parse(MockUpstreamState.LastRequestBody);
        var opts = forwarded?["options"];
        Assert.IsNotNull(opts, "options block must be present in the forwarded request");

        Assert.AreEqual(32768, opts["num_ctx"]?.GetValue<int>(),
            "num_ctx must survive deserialization and be forwarded");
        Assert.AreEqual(40, opts["top_k"]?.GetValue<int>(),
            "top_k must survive deserialization and be forwarded");
        Assert.AreEqual(0.9, opts["top_p"]!.GetValue<double>(), 0.01,
            "top_p must survive deserialization and be forwarded");
        Assert.AreEqual(0.7, opts["temperature"]!.GetValue<double>(), 0.01,
            "temperature must survive deserialization and be forwarded");
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
